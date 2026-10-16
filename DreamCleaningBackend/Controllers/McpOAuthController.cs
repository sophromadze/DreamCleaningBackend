using System.Net;
using System.Security.Claims;
using System.Text.Json;
using DreamCleaningBackend.Services.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace DreamCleaningBackend.Controllers
{
    /// <summary>
    /// The Claude connector's OAuth endpoints (2026-10) — see <see cref="McpOAuthService"/>.
    ///
    /// Discovery (RFC 9728 + RFC 8414), Dynamic Client Registration (RFC 7591), the authorize
    /// redirect, the token endpoint (form-encoded, RFC 6749) and revocation (RFC 7009) are called
    /// by Claude's servers, so they are anonymous and CORS-open (no cookie is ever read by them).
    /// The two <c>authorize/*</c> calls behind the consent page are the only ones that use the
    /// site's own sign-in.
    /// </summary>
    [ApiController]
    public class McpOAuthController : ControllerBase
    {
        public const string CorsPolicy = "McpPublic";
        public const string RegisterRateLimitPolicy = "mcp-register";
        public const string TokenRateLimitPolicy = "mcp-token";

        private readonly IMcpOAuthService _oauth;
        private readonly IConfiguration _configuration;

        public McpOAuthController(IMcpOAuthService oauth, IConfiguration configuration)
        {
            _oauth = oauth;
            _configuration = configuration;
        }

        // ── Discovery ───────────────────────────────────────────────────────────────────────────

        [HttpGet("/.well-known/oauth-protected-resource")]
        [HttpGet("/.well-known/oauth-protected-resource/api/mcp")]
        [AllowAnonymous]
        [EnableCors(CorsPolicy)]
        public IActionResult ProtectedResourceMetadata()
        {
            var baseUrl = McpOAuthPolicy.BaseUrl(_configuration);
            return Json(new
            {
                resource = McpOAuthPolicy.ResourceUrl(_configuration),
                authorization_servers = new[] { baseUrl },
                scopes_supported = new[] { McpOAuthPolicy.Scope },
                bearer_methods_supported = new[] { "header" },
                resource_name = McpToolCatalog.ServerTitle
            });
        }

        [HttpGet("/.well-known/oauth-authorization-server")]
        [AllowAnonymous]
        [EnableCors(CorsPolicy)]
        public IActionResult AuthorizationServerMetadata()
        {
            var baseUrl = McpOAuthPolicy.BaseUrl(_configuration);
            // No client_id_metadata_document_supported: Claude then registers through DCR, which
            // needs no outbound fetch from this server.
            return Json(new
            {
                issuer = baseUrl,
                authorization_endpoint = baseUrl + "/api/oauth/authorize",
                token_endpoint = baseUrl + "/api/oauth/token",
                registration_endpoint = baseUrl + "/api/oauth/register",
                revocation_endpoint = baseUrl + "/api/oauth/revoke",
                response_types_supported = new[] { "code" },
                grant_types_supported = new[] { "authorization_code", "refresh_token" },
                code_challenge_methods_supported = new[] { "S256" },
                token_endpoint_auth_methods_supported = new[] { "none" },
                revocation_endpoint_auth_methods_supported = new[] { "none" },
                scopes_supported = new[] { McpOAuthPolicy.Scope, McpOAuthPolicy.OfflineAccessScope },
                authorization_response_iss_parameter_supported = true
            });
        }

        // ── Registration ────────────────────────────────────────────────────────────────────────

        [HttpPost("/api/oauth/register")]
        [AllowAnonymous]
        [EnableCors(CorsPolicy)]
        [EnableRateLimiting(RegisterRateLimitPolicy)]
        public async Task<IActionResult> Register([FromBody] JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object)
                return OAuthError(new McpOAuthException("invalid_client_metadata", "Expected a JSON object."));

            var redirectUris = body.TryGetProperty("redirect_uris", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : new List<string>();
            var name = body.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var method = body.TryGetProperty("token_endpoint_auth_method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

            try
            {
                var client = await _oauth.RegisterClientAsync(name, redirectUris, method);
                return StatusCode(StatusCodes.Status201Created, new
                {
                    client_id = client.ClientId,
                    client_id_issued_at = new DateTimeOffset(DateTime.SpecifyKind(client.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                    client_name = client.ClientName,
                    redirect_uris = redirectUris,
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    token_endpoint_auth_method = "none",
                    scope = $"{McpOAuthPolicy.Scope} {McpOAuthPolicy.OfflineAccessScope}"
                });
            }
            catch (McpOAuthException ex)
            {
                return OAuthError(ex);
            }
        }

        // ── Authorization ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where Claude sends the browser. A bad client or redirect URI gets a plain page here and
        /// is never redirected (that would be an open redirect); any other problem goes back to the
        /// client as an OAuth error. A good request continues on the Angular consent page, which
        /// uses the site's normal sign-in.
        /// </summary>
        [HttpGet("/api/oauth/authorize")]
        [AllowAnonymous]
        public async Task<IActionResult> Authorize()
        {
            var query = Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            try
            {
                await _oauth.ValidateAuthorizeAsync(query);
            }
            catch (McpOAuthException ex) when (ex.Error is "invalid_client" or "invalid_redirect_uri")
            {
                return ErrorPage(ex.Message);
            }
            catch (McpOAuthException ex)
            {
                return Redirect(ErrorRedirect(query["redirect_uri"]!, ex.Error, ex.Message, query.GetValueOrDefault("state")));
            }

            var page = McpOAuthPolicy.BaseUrl(_configuration) + "/connect/authorize" + Request.QueryString.Value;
            return Redirect(page);
        }

        public sealed class AuthorizeDecisionDto
        {
            /// <summary>The query string the consent page was opened with (without '?').</summary>
            public string Query { get; set; } = string.Empty;
            public bool Approve { get; set; }
        }

        /// <summary>What the consent page shows: who is asking, where the answer goes, and whether
        /// the signed-in account may approve. Anonymous callers are told to sign in.</summary>
        [HttpPost("/api/oauth/authorize/check")]
        [AllowAnonymous]
        public async Task<IActionResult> CheckAuthorize([FromBody] AuthorizeDecisionDto dto)
        {
            try
            {
                var request = await _oauth.ValidateAuthorizeAsync(ParseQuery(dto.Query));
                var role = User.FindFirst(ClaimTypes.Role)?.Value ?? User.FindFirst("Role")?.Value;
                return Ok(new
                {
                    clientName = request.ClientName ?? "Claude",
                    redirectHost = new Uri(request.RedirectUri).Host,
                    signedIn = User.Identity?.IsAuthenticated == true,
                    canApprove = role == "SuperAdmin",
                    userName = User.FindFirst("FirstName")?.Value
                });
            }
            catch (McpOAuthException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        /// <summary>
        /// The consent page's Allow / Deny. Re-validates the whole request and re-checks SuperAdmin
        /// on the server; answers the URL the browser should go to next.
        /// </summary>
        [HttpPost("/api/oauth/authorize/decision")]
        [Authorize]
        public async Task<IActionResult> Decide([FromBody] AuthorizeDecisionDto dto)
        {
            McpAuthorizeRequest request;
            try
            {
                request = await _oauth.ValidateAuthorizeAsync(ParseQuery(dto.Query));
            }
            catch (McpOAuthException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var issuer = McpOAuthPolicy.BaseUrl(_configuration);
            if (!dto.Approve)
                return Ok(new { redirectUrl = ErrorRedirect(request.RedirectUri, "access_denied", "The user declined.", request.State) });

            var userId = int.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : 0;
            try
            {
                var code = await _oauth.IssueCodeAsync(request, userId);
                var url = QueryHelpers.AddQueryString(request.RedirectUri, new Dictionary<string, string?>
                {
                    ["code"] = code,
                    ["state"] = request.State,
                    ["iss"] = issuer
                }.Where(p => p.Value != null)!);
                return Ok(new { redirectUrl = url });
            }
            catch (McpOAuthException ex)
            {
                return StatusCode(ex.StatusCode, new { message = ex.Message });
            }
        }

        // ── Token + revocation ──────────────────────────────────────────────────────────────────

        [HttpPost("/api/oauth/token")]
        [AllowAnonymous]
        [EnableCors(CorsPolicy)]
        [EnableRateLimiting(TokenRateLimitPolicy)]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> Token([FromForm] IFormCollection form)
        {
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            string? F(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

            try
            {
                var tokens = F("grant_type") switch
                {
                    "authorization_code" => await _oauth.ExchangeCodeAsync(F("code"), F("client_id"), F("redirect_uri"), F("code_verifier"), F("resource")),
                    "refresh_token" => await _oauth.RefreshAsync(F("refresh_token"), F("client_id"), F("resource")),
                    _ => throw new McpOAuthException("unsupported_grant_type", "Use authorization_code or refresh_token.")
                };
                return Ok(new
                {
                    access_token = tokens.AccessToken,
                    token_type = "Bearer",
                    expires_in = tokens.ExpiresIn,
                    refresh_token = tokens.RefreshToken,
                    scope = tokens.Scope
                });
            }
            catch (McpOAuthException ex)
            {
                // A refused SuperAdmin check is still invalid_grant to the client (RFC 6749 §5.2).
                return OAuthError(ex.Error == "access_denied" ? new McpOAuthException("invalid_grant", ex.Message) : ex);
            }
        }

        [HttpPost("/api/oauth/revoke")]
        [AllowAnonymous]
        [EnableCors(CorsPolicy)]
        [EnableRateLimiting(TokenRateLimitPolicy)]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> Revoke([FromForm] IFormCollection form)
        {
            await _oauth.RevokeTokenAsync(form.TryGetValue("token", out var t) ? t.ToString() : null);
            return Ok(); // RFC 7009 §2.2: 200 whether or not the token existed.
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private IActionResult Json(object value) => new JsonResult(value) { ContentType = "application/json" };

        private IActionResult OAuthError(McpOAuthException ex)
        {
            var status = ex.Error == "invalid_client" ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest;
            return StatusCode(status, new { error = ex.Error, error_description = ex.Message });
        }

        private string ErrorRedirect(string redirectUri, string error, string description, string? state) =>
            QueryHelpers.AddQueryString(redirectUri, new Dictionary<string, string?>
            {
                ["error"] = error,
                ["error_description"] = description,
                ["state"] = state,
                ["iss"] = McpOAuthPolicy.BaseUrl(_configuration)
            }.Where(p => p.Value != null)!);

        private static Dictionary<string, string?> ParseQuery(string? query) =>
            QueryHelpers.ParseQuery(query ?? string.Empty).ToDictionary(q => q.Key, q => (string?)q.Value.ToString());

        private ContentResult ErrorPage(string message) => new()
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentType = "text/html; charset=utf-8",
            Content = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                      "<title>Connection failed</title></head><body style=\"font-family:system-ui,sans-serif;max-width:32rem;margin:3rem auto;padding:0 1rem\">" +
                      "<h1 style=\"font-size:1.25rem\">This connection request can't be completed</h1><p>" + WebUtility.HtmlEncode(message) +
                      "</p></body></html>"
        };
    }
}
