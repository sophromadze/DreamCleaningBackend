using System.Security.Cryptography;
using System.Text;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// The fixed rules of the Claude connector's OAuth server (2026-10), in one place.
    ///
    /// The public base URL is <c>Frontend:Url</c> — the site's own origin, which already names the
    /// domain every email link and the CORS policy use, so the connector needs no URL key of its
    /// own. The MCP endpoint is <c>{base}/api/mcp</c>, the issuer is <c>{base}</c>, and the
    /// consent page is the Angular route <c>{base}/connect/authorize</c>. Apache already proxies
    /// <c>/api</c> to Kestrel; only the <c>/.well-known/oauth-*</c> documents need a new rule.
    /// </summary>
    public static class McpOAuthPolicy
    {
        public const string McpPath = "/api/mcp";
        public const string Scope = "mcp";
        public const string OfflineAccessScope = "offline_access";

        public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);

        /// <summary>Idle limit: a refresh token not used for this long ends the session.</summary>
        public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

        public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

        /// <summary>The hosted Claude apps (claude.ai web, Desktop, mobile, Cowork) use this one
        /// callback; claude.com is accepted too in case the apps move to that host.</summary>
        public static readonly IReadOnlyList<string> AllowedRedirectUris = new[]
        {
            "https://claude.ai/api/mcp/auth_callback",
            "https://claude.com/api/mcp/auth_callback"
        };

        public const string FallbackBaseUrl = "https://dreamcleaningnyc.com";
        public const string TokenPrefix = "dcmcp_";

        /// <summary>The site's public origin, without a trailing slash.</summary>
        public static string BaseUrl(IConfiguration configuration)
        {
            var configured = configuration["Frontend:Url"];
            var url = string.IsNullOrWhiteSpace(configured) ? FallbackBaseUrl : configured.Trim();
            return url.TrimEnd('/');
        }

        public static string ResourceUrl(IConfiguration configuration) => BaseUrl(configuration) + McpPath;

        public static string ResourceMetadataUrl(IConfiguration configuration) =>
            BaseUrl(configuration) + "/.well-known/oauth-protected-resource" + McpPath;

        /// <summary>
        /// Whether <paramref name="redirectUri"/> may be registered. The Claude callbacks always;
        /// a loopback URI (MCP Inspector, Claude Code) only in Development.
        /// </summary>
        public static bool IsAllowedRedirectUri(string? redirectUri, bool isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(redirectUri)) return false;
            if (AllowedRedirectUris.Contains(redirectUri, StringComparer.Ordinal)) return true;
            return isDevelopment && IsLoopback(redirectUri);
        }

        public static bool IsLoopback(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttp
            && (u.Host == "localhost" || u.Host == "127.0.0.1" || u.Host == "[::1]")
            && string.IsNullOrEmpty(u.Fragment);

        /// <summary>
        /// Exact match against a registered URI, except a loopback URI, which RFC 8252 §7.3 says to
        /// compare with the port ignored (native clients pick a port per run).
        /// </summary>
        public static bool RedirectMatches(string registered, string requested)
        {
            if (string.Equals(registered, requested, StringComparison.Ordinal)) return true;
            if (!IsLoopback(registered) || !IsLoopback(requested)) return false;
            var a = new Uri(registered);
            var b = new Uri(requested);
            return a.Host == b.Host && a.AbsolutePath == b.AbsolutePath && a.Query == b.Query;
        }

        /// <summary>The <c>resource</c> parameter (RFC 8707), when sent, must name this server.</summary>
        public static bool ResourceMatches(string? resource, IConfiguration configuration) =>
            string.IsNullOrWhiteSpace(resource)
            || string.Equals(resource.TrimEnd('/'), ResourceUrl(configuration), StringComparison.OrdinalIgnoreCase);

        /// <summary>Scopes we grant: "mcp", plus "offline_access" when asked. Anything else is refused.</summary>
        public static string? NormalizeScope(string? requested)
        {
            var parts = (requested ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal).ToList();
            if (parts.Any(p => p != Scope && p != OfflineAccessScope)) return null;
            return parts.Contains(OfflineAccessScope) ? $"{Scope} {OfflineAccessScope}" : Scope;
        }

        public static string NewSecret(string kind)
        {
            Span<byte> bytes = stackalloc byte[32];
            RandomNumberGenerator.Fill(bytes);
            return TokenPrefix + kind + "_" + Base64Url(bytes);
        }

        public static string NewClientId()
        {
            Span<byte> bytes = stackalloc byte[18];
            RandomNumberGenerator.Fill(bytes);
            return "mcpc_" + Base64Url(bytes);
        }

        public static string Hash(string secret) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

        /// <summary>PKCE S256: BASE64URL(SHA256(verifier)) == challenge, in constant time.</summary>
        public static bool VerifyPkce(string? verifier, string challenge)
        {
            // RFC 7636 §4.1: 43-128 characters of [A-Z a-z 0-9 - . _ ~].
            if (string.IsNullOrEmpty(verifier) || verifier.Length is < 43 or > 128) return false;
            if (verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))) return false;
            var computed = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
        }

        public static string Base64Url(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
