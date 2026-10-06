namespace DreamCleaningBackend.Helpers
{
    /// <summary>The first-touch facts an order carries, as the classifier reads them.</summary>
    public sealed record OrderSourceSignals(
        bool BookedByAdmin,
        string? Channel,
        string? Source,
        string? Medium,
        string? ClickIdType,
        string? Referrer);

    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "where did this order come from?" (2026-10) - the Company →
    /// Order Sources report. Nothing else may bucket orders into these sources.
    ///
    /// It classifies from the RAW first-touch signals stored on the order (click id, UTM
    /// source/medium, referrer) rather than trusting the browser's channel verdict, and falls back
    /// to the stored <c>AcquisitionChannel</c> only when the raw signals say nothing. That fallback
    /// is what lets historical orders - captured by the cookie before the raw columns existed, or
    /// back-filled from GA4 - be classified instead of all reading Unknown.
    ///
    /// Precedence, first match wins:
    /// <list type="number">
    /// <item><b>Admin-booked</b> - <c>BookedByAdminUserId</c> is set (or the legacy
    /// "Phone/Unknown" stamp). The browser that placed it was an admin's, so no web signal on
    /// it is the customer's.</item>
    /// <item><b>Paid</b> - a Google Ads click id (gclid / gbraid / wbraid), a Microsoft Ads one
    /// (msclkid), a paid UTM medium (cpc, ppc, paid, ...), or a stored "Paid Search" channel.
    /// A paid click whose referrer is google.com is still paid, which is why this precedes
    /// the search check.</item>
    /// <item><b>AI assistant</b>, <b>Email</b>, <b>Social</b> - checked before search, because
    /// gemini.google.com is an AI host under a search-engine domain.</item>
    /// <item><b>Organic search</b> - a search-engine referrer/source, or utm_medium=organic,
    /// with no paid signal.</item>
    /// <item><b>Direct</b> - "(direct)" / "(none)", no referrer, or our own site as referrer.</item>
    /// <item><b>Referral</b> - any other external site.</item>
    /// <item>Otherwise the stored channel, and <b>Unknown</b> when there is nothing at all
    /// (every order from before attribution was captured, unless GA4 filled it in).</item>
    /// </list>
    /// The host lists match the browser's <c>AttributionService</c>, which still labels sessions
    /// for the CRM Ads funnel; keep them in step.
    /// </summary>
    public static class OrderSourceClassifier
    {
        public const string Paid = "paid";
        public const string Organic = "organic";
        public const string Direct = "direct";
        public const string Referral = "referral";
        public const string Social = "social";
        public const string Email = "email";
        public const string AiAssistant = "ai";
        public const string AdminBooked = "admin";
        public const string Unknown = "unknown";

        /// <summary>Every source in display order, with its report label.</summary>
        public static readonly IReadOnlyList<(string Key, string Label)> All = new[]
        {
            (Paid, "Paid ads"),
            (Organic, "Organic search"),
            (Direct, "Direct"),
            (Referral, "Referral"),
            (Social, "Social"),
            (Email, "Email"),
            (AiAssistant, "AI assistant"),
            (AdminBooked, "Admin-booked"),
            (Unknown, "Unknown"),
        };

        /// <summary>Click ids that only an ad click puts on a landing URL.</summary>
        public static readonly IReadOnlyList<string> PaidClickIds = new[] { "gclid", "gbraid", "wbraid", "msclkid" };

        private static readonly HashSet<string> PaidMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "cpc", "ppc", "paid", "paidsearch", "paid_search", "paid-search", "sem", "cpm", "display",
            "paid_social", "paid-social", "paidsocial"
        };

        private static readonly HashSet<string> EmailMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "email", "e-mail", "newsletter"
        };

        private static readonly HashSet<string> SocialMediums = new(StringComparer.OrdinalIgnoreCase)
        {
            "social", "social-media", "social_media", "social-network", "sm"
        };

        private static readonly string[] AiHosts =
        {
            "chat.openai.com", "chatgpt.com", "gemini.google.com", "claude.ai", "perplexity.ai",
            "copilot.microsoft.com", "grok.com", "x.ai", "chat.deepseek.com"
        };

        private static readonly string[] SocialHosts =
        {
            "facebook.com", "instagram.com", "twitter.com", "x.com", "t.co", "linkedin.com", "lnkd.in",
            "tiktok.com", "pinterest.com", "reddit.com", "youtube.com", "threads.net", "snapchat.com",
            "wa.me", "t.me"
        };

        // Substrings of a search-engine host ("www.google.com" contains "google.").
        private static readonly string[] SearchHostParts = { "google.", "bing.", "yahoo.", "duckduckgo.", "baidu." };

        // A UTM source written as a bare engine name ("google") rather than a host.
        private static readonly HashSet<string> SearchSourceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "google", "bing", "yahoo", "duckduckgo", "baidu"
        };

        /// <summary>
        /// Normalises a client-sent click id type to one of <see cref="PaidClickIds"/>, or null.
        /// </summary>
        public static string? NormalizeClickIdType(string? clickIdType)
        {
            if (string.IsNullOrWhiteSpace(clickIdType)) return null;
            var trimmed = clickIdType.Trim().ToLowerInvariant();
            return PaidClickIds.Contains(trimmed) ? trimmed : null;
        }

        /// <param name="ownHost">Our own site's host (from <c>Frontend:Url</c>), so a referrer
        /// from our own pages reads as Direct rather than Referral. Optional.</param>
        public static string Classify(OrderSourceSignals s, string? ownHost = null)
        {
            if (s.BookedByAdmin || IsChannel(s.Channel, AcquisitionChannels.AdminManual))
                return AdminBooked;

            var medium = (s.Medium ?? "").Trim();
            var source = (s.Source ?? "").Trim();

            if (NormalizeClickIdType(s.ClickIdType) != null
                || PaidMediums.Contains(medium)
                || IsChannel(s.Channel, "Paid Search"))
                return Paid;

            // The external host the visit came from: the stored referrer when we have one, else a
            // UTM source / referrer host recorded in Source.
            var host = HostOf(s.Referrer) ?? HostOf(source);
            var own = BareHost(ownHost);
            var isOwnHost = host != null && own != null && BareHost(host) == own;

            if (host != null && !isOwnHost && MatchesHost(host, AiHosts)) return AiAssistant;
            if (EmailMediums.Contains(medium)) return Email;
            if (SocialMediums.Contains(medium) || (host != null && !isOwnHost && MatchesHost(host, SocialHosts)))
                return Social;
            if (string.Equals(medium, "organic", StringComparison.OrdinalIgnoreCase)
                || SearchSourceNames.Contains(source)
                || (host != null && !isOwnHost && SearchHostParts.Any(p => host.Contains(p))))
                return Organic;

            // "(direct)" / "(none)" are the placeholders the browser and GA4 record for a visit with
            // no referrer and no UTM. A blank row is NOT direct - it is an order with no attribution.
            if (isOwnHost) return Direct;
            if (host == null && (source == "(direct)" || medium == "(none)")) return Direct;

            if (host != null) return Referral;

            return FromChannel(s.Channel);
        }

        /// <summary>The report label for a source key.</summary>
        public static string LabelOf(string key) =>
            All.FirstOrDefault(a => a.Key == key).Label ?? "Unknown";

        // Last resort: the channel the browser (or GA4) recorded.
        private static string FromChannel(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return Unknown;
            return channel.Trim().ToLowerInvariant() switch
            {
                "paid search" => Paid,
                "organic search" => Organic,
                "direct" => Direct,
                "referral" => Referral,
                "social" or "organic social" => Social,
                "email" => Email,
                "ai assistant" => AiAssistant,
                _ => Unknown
            };
        }

        private static bool IsChannel(string? channel, string expected) =>
            string.Equals(channel?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

        private static bool MatchesHost(string host, string[] hosts) =>
            hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));

        // A host from a URL ("https://www.google.com/search") or a bare host ("google.com").
        // Returns null for placeholders like "(direct)" and for plain words ("google", "newsletter").
        private static string? HostOf(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.StartsWith('(')) return null;
            if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                return uri.Host.ToLowerInvariant();
            if (!v.Contains('.') || v.Contains(' ')) return null;
            var candidate = v.Split('/')[0].ToLowerInvariant();
            return Uri.CheckHostName(candidate) == UriHostNameType.Dns ? candidate : null;
        }

        private static string? BareHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            var h = HostOf(host) ?? host.Trim().ToLowerInvariant();
            return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
        }
    }
}
