using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>
    /// Authentication for <c>/api/mcp</c> ONLY (2026-10). Accepts the connector's own opaque access
    /// tokens and nothing else: the site's JWT is never valid here, and these tokens are never
    /// valid on the rest of <c>/api</c> (the JwtBearer scheme cannot parse them).
    ///
    /// Every request re-runs <see cref="IMcpOAuthService.AuthenticateAsync"/>, which re-reads the
    /// user's role and TokenVersion — in stateless mode each tool call is its own request, so a
    /// demoted SuperAdmin's very next call is refused.
    ///
    /// The challenge is what starts sign-in in Claude: <c>401</c> with
    /// <c>WWW-Authenticate: Bearer resource_metadata="…"</c> (Claude ignores the header on any other
    /// status).
    /// </summary>
    public sealed class McpBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "McpBearer";
        public const string GrantIdClaim = "McpGrantId";

        private readonly IMcpOAuthService _oauth;
        private readonly IConfiguration _configuration;

        public McpBearerHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
            IMcpOAuthService oauth, IConfiguration configuration)
            : base(options, logger, encoder)
        {
            _oauth = oauth;
            _configuration = configuration;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.NoResult();

            var token = header["Bearer ".Length..].Trim();
            var caller = await _oauth.AuthenticateAsync(token);
            if (caller == null)
            {
                Context.Items[nameof(McpBearerHandler)] = "invalid_token";
                return AuthenticateResult.Fail("Invalid, expired or revoked connector token.");
            }

            var identity = new ClaimsIdentity(new[]
            {
                // The same claim names the site's JWT carries, so AuditService and the expense
                // service attribute what the connector does to the right person.
                new Claim(ClaimTypes.NameIdentifier, caller.UserId.ToString()),
                new Claim("UserId", caller.UserId.ToString()),
                new Claim(ClaimTypes.Name, caller.Name),
                new Claim(ClaimTypes.Role, "SuperAdmin"),
                new Claim(GrantIdClaim, caller.GrantId.ToString())
            }, SchemeName);
            Context.Items[typeof(McpCaller)] = caller;
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            var metadata = McpOAuthPolicy.ResourceMetadataUrl(_configuration);
            var value = Context.Items.ContainsKey(nameof(McpBearerHandler))
                ? $"Bearer error=\"invalid_token\", error_description=\"The token is invalid, expired or revoked\", resource_metadata=\"{metadata}\", scope=\"{McpOAuthPolicy.Scope}\""
                : $"Bearer resource_metadata=\"{metadata}\", scope=\"{McpOAuthPolicy.Scope}\"";
            Response.Headers.WWWAuthenticate = value;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            // Never reached in practice (the policy only needs an authenticated caller), but a 403
            // without a challenge would leave Claude with a dead connection rather than a sign-in.
            return HandleChallengeAsync(properties);
        }
    }
}
