using System.Text.Json;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using Microsoft.EntityFrameworkCore;

namespace DreamCleaningBackend.Services.Mcp
{
    /// <summary>An OAuth error answered to the client: RFC 6749 code + description + HTTP status.</summary>
    public sealed class McpOAuthException : Exception
    {
        public string Error { get; }
        public int StatusCode { get; }

        public McpOAuthException(string error, string description, int statusCode = 400) : base(description)
        {
            Error = error;
            StatusCode = statusCode;
        }
    }

    /// <summary>A validated authorization request (the query the consent page was opened with).</summary>
    public sealed record McpAuthorizeRequest(
        string ClientId, string? ClientName, string RedirectUri, string CodeChallenge, string Scope, string? State);

    public sealed record McpTokenResponse(string AccessToken, int ExpiresIn, string RefreshToken, string Scope);

    /// <summary>Who an access token belongs to — set on the request by <see cref="McpBearerHandler"/>.</summary>
    public sealed record McpCaller(int UserId, int GrantId, string Name);

    public sealed class McpSessionDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string? ClientName { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public int ToolCalls { get; set; }
    }

    public interface IMcpOAuthService
    {
        Task<McpOAuthClient> RegisterClientAsync(string? clientName, IReadOnlyList<string> redirectUris, string? tokenEndpointAuthMethod);

        /// <summary>Validates an authorization request. Throws <see cref="McpOAuthException"/>;
        /// <c>invalid_client</c>/<c>invalid_redirect_uri</c> must NOT be redirected back.</summary>
        Task<McpAuthorizeRequest> ValidateAuthorizeAsync(IReadOnlyDictionary<string, string?> query);

        /// <summary>Issues a code for an approving SuperAdmin; returns the code.</summary>
        Task<string> IssueCodeAsync(McpAuthorizeRequest request, int userId);

        Task<McpTokenResponse> ExchangeCodeAsync(string? code, string? clientId, string? redirectUri, string? codeVerifier, string? resource);
        Task<McpTokenResponse> RefreshAsync(string? refreshToken, string? clientId, string? resource);
        Task RevokeTokenAsync(string? token);

        /// <summary>
        /// The per-request check: a live access token, of a live session, of an active SuperAdmin
        /// whose TokenVersion has not moved. A session that fails the USER half is revoked on the
        /// spot, so the admin panel shows why. Null = refuse.
        /// </summary>
        Task<McpCaller?> AuthenticateAsync(string accessToken);

        /// <summary>The same user checks, without a token — re-run before every tool call.</summary>
        Task<bool> IsGrantStillValidAsync(int grantId, int userId);

        Task<List<McpSessionDto>> ListActiveSessionsAsync();
        Task<bool> RevokeGrantAsync(int grantId, int byUserId, string reason);
    }

    /// <summary>
    /// The Claude connector's small OAuth 2.1 authorization server (2026-10). Hand-built because
    /// the surface is narrow and DCR is required (Claude registers itself): authorization code +
    /// PKCE S256 only, public clients only, opaque tokens stored as SHA-256 hashes.
    ///
    /// Security rules it holds:
    /// <list type="bullet">
    /// <item>SuperAdmin is checked when the code is issued, when it is exchanged, on every refresh
    /// and on every MCP request — a demoted, blocked or deleted account loses access at once;</item>
    /// <item>a session is tied to the user's <see cref="User.TokenVersion"/>: anything that ends
    /// the user's site sessions (role change, sign out everywhere, password change) ends it;</item>
    /// <item>codes and refresh tokens are single-use, claimed with a conditional UPDATE so two racing
    /// requests cannot both succeed; a replayed code or refresh token revokes the session.</item>
    /// </list>
    /// </summary>
    public sealed class McpOAuthService : IMcpOAuthService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<McpOAuthService> _logger;

        public McpOAuthService(ApplicationDbContext context, IConfiguration configuration, IHostEnvironment environment, ILogger<McpOAuthService> logger)
        {
            _context = context;
            _configuration = configuration;
            _environment = environment;
            _logger = logger;
        }

        // ── Registration (RFC 7591) ─────────────────────────────────────────────────────────────

        public async Task<McpOAuthClient> RegisterClientAsync(string? clientName, IReadOnlyList<string> redirectUris, string? tokenEndpointAuthMethod)
        {
            if (!string.IsNullOrEmpty(tokenEndpointAuthMethod) && tokenEndpointAuthMethod != "none")
                throw new McpOAuthException("invalid_client_metadata", "Only public clients (token_endpoint_auth_method \"none\") are supported.");
            if (redirectUris.Count is 0 or > 5)
                throw new McpOAuthException("invalid_redirect_uri", "Register between 1 and 5 redirect URIs.");
            foreach (var uri in redirectUris)
                if (!McpOAuthPolicy.IsAllowedRedirectUri(uri, _environment.IsDevelopment()))
                    throw new McpOAuthException("invalid_redirect_uri", $"Redirect URI not allowed: {uri}");

            var name = string.IsNullOrWhiteSpace(clientName) ? null : clientName.Trim();
            if (name?.Length > 200) name = name[..200];

            var client = new McpOAuthClient
            {
                ClientId = McpOAuthPolicy.NewClientId(),
                ClientName = name,
                RedirectUrisJson = JsonSerializer.Serialize(redirectUris),
                CreatedAt = DateTime.UtcNow
            };
            _context.McpOAuthClients.Add(client);
            await _context.SaveChangesAsync();
            _logger.LogInformation("Claude connector: OAuth client {ClientId} registered ({ClientName})", client.ClientId, name ?? "unnamed");
            return client;
        }

        // ── Authorization ───────────────────────────────────────────────────────────────────────

        public async Task<McpAuthorizeRequest> ValidateAuthorizeAsync(IReadOnlyDictionary<string, string?> query)
        {
            string? Get(string key) => query.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

            var clientId = Get("client_id") ?? throw new McpOAuthException("invalid_client", "client_id is missing.");
            var client = await _context.McpOAuthClients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientId)
                ?? throw new McpOAuthException("invalid_client", "Unknown client. Remove the connector in Claude and add it again.");

            var redirectUri = Get("redirect_uri") ?? throw new McpOAuthException("invalid_redirect_uri", "redirect_uri is missing.");
            var registered = JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? new List<string>();
            if (!registered.Any(r => McpOAuthPolicy.RedirectMatches(r, redirectUri)))
                throw new McpOAuthException("invalid_redirect_uri", "redirect_uri does not match the registered client.");

            // From here on, errors may be sent back to the (verified) redirect URI.
            if (Get("response_type") != "code")
                throw new McpOAuthException("unsupported_response_type", "Only response_type=code is supported.");
            var challenge = Get("code_challenge") ?? throw new McpOAuthException("invalid_request", "PKCE code_challenge is required.");
            if (Get("code_challenge_method") != "S256")
                throw new McpOAuthException("invalid_request", "code_challenge_method must be S256.");
            if (challenge.Length is < 43 or > 128)
                throw new McpOAuthException("invalid_request", "code_challenge is malformed.");
            if (!McpOAuthPolicy.ResourceMatches(Get("resource"), _configuration))
                throw new McpOAuthException("invalid_target", "resource does not name this server.");
            var scope = McpOAuthPolicy.NormalizeScope(Get("scope"))
                ?? throw new McpOAuthException("invalid_scope", "Only the \"mcp\" and \"offline_access\" scopes exist.");

            return new McpAuthorizeRequest(client.ClientId, client.ClientName, redirectUri, challenge, scope, Get("state"));
        }

        public async Task<string> IssueCodeAsync(McpAuthorizeRequest request, int userId)
        {
            await RequireSuperAdminAsync(userId);
            var code = McpOAuthPolicy.NewSecret("code");
            _context.McpAuthorizationCodes.Add(new McpAuthorizationCode
            {
                CodeHash = McpOAuthPolicy.Hash(code),
                ClientId = request.ClientId,
                UserId = userId,
                RedirectUri = request.RedirectUri,
                CodeChallenge = request.CodeChallenge,
                Scope = request.Scope,
                ExpiresAt = DateTime.UtcNow.Add(McpOAuthPolicy.AuthorizationCodeLifetime),
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return code;
        }

        // ── Token endpoint ──────────────────────────────────────────────────────────────────────

        public async Task<McpTokenResponse> ExchangeCodeAsync(string? code, string? clientId, string? redirectUri, string? codeVerifier, string? resource)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
                throw new McpOAuthException("invalid_request", "code, client_id and redirect_uri are required.");
            if (!McpOAuthPolicy.ResourceMatches(resource, _configuration))
                throw new McpOAuthException("invalid_target", "resource does not name this server.");

            var hash = McpOAuthPolicy.Hash(code);
            var row = await _context.McpAuthorizationCodes.FirstOrDefaultAsync(c => c.CodeHash == hash)
                ?? throw new McpOAuthException("invalid_grant", "Unknown authorization code.");

            if (row.ConsumedAt != null)
            {
                // RFC 6749 §4.1.2: a replayed code SHOULD revoke what it was exchanged for.
                if (row.GrantId is { } replayedGrant)
                    await RevokeGrantAsync(replayedGrant, null, "Authorization code replayed");
                throw new McpOAuthException("invalid_grant", "Authorization code already used.");
            }
            if (row.ExpiresAt <= DateTime.UtcNow)
                throw new McpOAuthException("invalid_grant", "Authorization code expired.");
            if (row.ClientId != clientId)
                throw new McpOAuthException("invalid_grant", "Authorization code was issued to another client.");
            if (!string.Equals(row.RedirectUri, redirectUri, StringComparison.Ordinal))
                throw new McpOAuthException("invalid_grant", "redirect_uri does not match the authorization request.");
            if (!McpOAuthPolicy.VerifyPkce(codeVerifier, row.CodeChallenge))
                throw new McpOAuthException("invalid_grant", "PKCE verification failed.");

            // Claim the code atomically: only one of two racing exchanges sees a row change.
            var now = DateTime.UtcNow;
            var claimed = await _context.McpAuthorizationCodes
                .Where(c => c.Id == row.Id && c.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now));
            if (claimed == 0)
                throw new McpOAuthException("invalid_grant", "Authorization code already used.");

            var user = await RequireSuperAdminAsync(row.UserId);
            var client = await _context.McpOAuthClients.FirstOrDefaultAsync(c => c.ClientId == clientId);

            var grant = new McpGrant
            {
                UserId = user.Id,
                ClientId = clientId,
                ClientName = client?.ClientName,
                TokenVersion = user.TokenVersion,
                Scope = row.Scope,
                CreatedAt = now,
                LastUsedAt = now
            };
            _context.McpGrants.Add(grant);
            if (client != null) client.LastUsedAt = now;
            await _context.SaveChangesAsync();

            await _context.McpAuthorizationCodes.Where(c => c.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.GrantId, grant.Id));

            _logger.LogInformation("Claude connector: session {GrantId} started for user {UserId} (client {ClientId})", grant.Id, user.Id, clientId);
            return await IssueTokensAsync(grant);
        }

        public async Task<McpTokenResponse> RefreshAsync(string? refreshToken, string? clientId, string? resource)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new McpOAuthException("invalid_request", "refresh_token is required.");
            if (!McpOAuthPolicy.ResourceMatches(resource, _configuration))
                throw new McpOAuthException("invalid_target", "resource does not name this server.");

            var hash = McpOAuthPolicy.Hash(refreshToken);
            var token = await _context.McpTokens.Include(t => t.Grant)
                .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == McpTokenKinds.Refresh);
            if (token?.Grant == null)
                throw new McpOAuthException("invalid_grant", "Unknown refresh token.");
            var grant = token.Grant;

            if (token.ConsumedAt != null)
            {
                // A used refresh token presented again means a copy exists somewhere: end the session.
                await RevokeGrantAsync(grant.Id, null, "Refresh token reused");
                throw new McpOAuthException("invalid_grant", "Refresh token already used; the session was ended.");
            }
            if (grant.RevokedAt != null)
                throw new McpOAuthException("invalid_grant", "The session was revoked.");
            if (token.ExpiresAt <= DateTime.UtcNow)
                throw new McpOAuthException("invalid_grant", "Refresh token expired.");
            if (!string.IsNullOrWhiteSpace(clientId) && clientId != grant.ClientId)
                throw new McpOAuthException("invalid_grant", "Refresh token was issued to another client.");

            if (!await UserStillAllowedAsync(grant))
            {
                await RevokeGrantAsync(grant.Id, null, "No longer a SuperAdmin or signed out everywhere");
                throw new McpOAuthException("invalid_grant", "This account can no longer use the Claude connector.");
            }

            var now = DateTime.UtcNow;
            var claimed = await _context.McpTokens
                .Where(t => t.Id == token.Id && t.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now));
            if (claimed == 0)
            {
                await RevokeGrantAsync(grant.Id, null, "Refresh token reused");
                throw new McpOAuthException("invalid_grant", "Refresh token already used; the session was ended.");
            }

            // The previous access tokens of this grant go with the rotation.
            await _context.McpTokens
                .Where(t => t.GrantId == grant.Id && t.Kind == McpTokenKinds.Access && t.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now));

            return await IssueTokensAsync(grant);
        }

        public async Task RevokeTokenAsync(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            var hash = McpOAuthPolicy.Hash(token);
            var grantId = await _context.McpTokens.Where(t => t.TokenHash == hash).Select(t => (int?)t.GrantId).FirstOrDefaultAsync();
            if (grantId != null)
                await RevokeGrantAsync(grantId.Value, null, "Revoked by the client");
        }

        private async Task<McpTokenResponse> IssueTokensAsync(McpGrant grant)
        {
            var now = DateTime.UtcNow;
            var access = McpOAuthPolicy.NewSecret("at");
            var refresh = McpOAuthPolicy.NewSecret("rt");
            _context.McpTokens.Add(new McpToken
            {
                GrantId = grant.Id,
                Kind = McpTokenKinds.Access,
                TokenHash = McpOAuthPolicy.Hash(access),
                ExpiresAt = now.Add(McpOAuthPolicy.AccessTokenLifetime),
                CreatedAt = now
            });
            _context.McpTokens.Add(new McpToken
            {
                GrantId = grant.Id,
                Kind = McpTokenKinds.Refresh,
                TokenHash = McpOAuthPolicy.Hash(refresh),
                ExpiresAt = now.Add(McpOAuthPolicy.RefreshTokenLifetime),
                CreatedAt = now
            });
            await _context.SaveChangesAsync();
            return new McpTokenResponse(access, (int)McpOAuthPolicy.AccessTokenLifetime.TotalSeconds, refresh, grant.Scope ?? McpOAuthPolicy.Scope);
        }

        // ── Resource-server side ────────────────────────────────────────────────────────────────

        public async Task<McpCaller?> AuthenticateAsync(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || !accessToken.StartsWith(McpOAuthPolicy.TokenPrefix, StringComparison.Ordinal))
                return null;

            var hash = McpOAuthPolicy.Hash(accessToken);
            var now = DateTime.UtcNow;
            var row = await _context.McpTokens.AsNoTracking()
                .Where(t => t.TokenHash == hash && t.Kind == McpTokenKinds.Access && t.ExpiresAt > now)
                .Select(t => new
                {
                    t.GrantId,
                    t.Grant!.UserId,
                    t.Grant.RevokedAt,
                    t.Grant.LastUsedAt,
                    GrantTokenVersion = t.Grant.TokenVersion,
                    t.Grant.User!.Role,
                    t.Grant.User.IsActive,
                    t.Grant.User.IsDeleted,
                    UserTokenVersion = t.Grant.User.TokenVersion,
                    t.Grant.User.FirstName,
                    t.Grant.User.LastName
                })
                .FirstOrDefaultAsync();
            if (row == null || row.RevokedAt != null) return null;

            if (row.Role != UserRole.SuperAdmin || !row.IsActive || row.IsDeleted || row.UserTokenVersion != row.GrantTokenVersion)
            {
                await RevokeGrantAsync(row.GrantId, null, "No longer a SuperAdmin or signed out everywhere");
                return null;
            }

            // "Last used" for the sessions list, written at most once a minute per session.
            if (row.LastUsedAt == null || row.LastUsedAt < now.AddMinutes(-1))
                await _context.McpGrants.Where(g => g.Id == row.GrantId)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastUsedAt, now));

            return new McpCaller(row.UserId, row.GrantId, $"{row.FirstName} {row.LastName}".Trim());
        }

        public async Task<bool> IsGrantStillValidAsync(int grantId, int userId)
        {
            var row = await _context.McpGrants.AsNoTracking()
                .Where(g => g.Id == grantId && g.UserId == userId)
                .Select(g => new { g.RevokedAt, g.TokenVersion, g.User!.Role, g.User.IsActive, g.User.IsDeleted, UserTokenVersion = g.User.TokenVersion })
                .FirstOrDefaultAsync();
            if (row == null || row.RevokedAt != null) return false;
            if (row.Role != UserRole.SuperAdmin || !row.IsActive || row.IsDeleted || row.UserTokenVersion != row.TokenVersion)
            {
                await RevokeGrantAsync(grantId, null, "No longer a SuperAdmin or signed out everywhere");
                return false;
            }
            return true;
        }

        // ── Sessions (admin panel) ──────────────────────────────────────────────────────────────

        public async Task<List<McpSessionDto>> ListActiveSessionsAsync()
        {
            var now = DateTime.UtcNow;
            var grants = await _context.McpGrants.AsNoTracking()
                .Where(g => g.RevokedAt == null
                            && g.Tokens.Any(t => t.Kind == McpTokenKinds.Refresh && t.ConsumedAt == null && t.ExpiresAt > now))
                .OrderByDescending(g => g.LastUsedAt ?? g.CreatedAt)
                .Select(g => new McpSessionDto
                {
                    Id = g.Id,
                    UserId = g.UserId,
                    UserName = (g.User!.FirstName + " " + g.User.LastName).Trim(),
                    ClientName = g.ClientName,
                    ClientId = g.ClientId,
                    CreatedAt = g.CreatedAt,
                    LastUsedAt = g.LastUsedAt,
                    ExpiresAt = g.Tokens.Where(t => t.Kind == McpTokenKinds.Refresh && t.ConsumedAt == null).Max(t => (DateTime?)t.ExpiresAt)
                })
                .ToListAsync();

            if (grants.Count > 0)
            {
                var ids = grants.Select(g => g.Id).ToList();
                var counts = await _context.McpToolCallLogs.AsNoTracking()
                    .Where(l => ids.Contains(l.GrantId))
                    .GroupBy(l => l.GrantId)
                    .Select(g => new { g.Key, Count = g.Count() })
                    .ToDictionaryAsync(x => x.Key, x => x.Count);
                foreach (var g in grants)
                    g.ToolCalls = counts.GetValueOrDefault(g.Id);
            }
            return grants;
        }

        public async Task<bool> RevokeGrantAsync(int grantId, int? byUserId, string reason)
        {
            var now = DateTime.UtcNow;
            var changed = await _context.McpGrants
                .Where(g => g.Id == grantId && g.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.RevokedAt, now)
                    .SetProperty(g => g.RevokedReason, reason.Length > 100 ? reason[..100] : reason)
                    .SetProperty(g => g.RevokedByUserId, byUserId));
            // Expire every token at once; the per-request check would refuse them anyway.
            await _context.McpTokens
                .Where(t => t.GrantId == grantId && t.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now));
            if (changed > 0)
                _logger.LogInformation("Claude connector: session {GrantId} revoked ({Reason})", grantId, reason);
            return changed > 0;
        }

        Task<bool> IMcpOAuthService.RevokeGrantAsync(int grantId, int byUserId, string reason) =>
            RevokeGrantAsync(grantId, byUserId, reason);

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private async Task<User> RequireSuperAdminAsync(int userId)
        {
            var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null || user.Role != UserRole.SuperAdmin || !user.IsActive || user.IsDeleted)
                throw new McpOAuthException("access_denied", "Only SuperAdmin accounts can connect Claude to this site.", 403);
            return user;
        }

        private async Task<bool> UserStillAllowedAsync(McpGrant grant)
        {
            var user = await _context.Users.AsNoTracking()
                .Where(u => u.Id == grant.UserId)
                .Select(u => new { u.Role, u.IsActive, u.IsDeleted, u.TokenVersion })
                .FirstOrDefaultAsync();
            return user != null && user.Role == UserRole.SuperAdmin && user.IsActive && !user.IsDeleted
                   && user.TokenVersion == grant.TokenVersion;
        }
    }
}
