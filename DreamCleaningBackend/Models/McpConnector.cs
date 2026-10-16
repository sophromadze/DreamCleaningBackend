using System.ComponentModel.DataAnnotations;

namespace DreamCleaningBackend.Models
{
    /// <summary>
    /// An OAuth client that registered itself for the Claude connector (2026-10) through Dynamic
    /// Client Registration. Public client only (PKCE, no secret), and only with redirect URIs on
    /// the allowlist in <c>McpOAuthPolicy</c>, so a registration on its own grants nothing: every
    /// authorization still needs a signed-in SuperAdmin to approve it.
    /// </summary>
    public class McpOAuthClient
    {
        public int Id { get; set; }

        /// <summary>The random public identifier handed back to the client.</summary>
        [Required]
        [StringLength(64)]
        public string ClientId { get; set; } = string.Empty;

        [StringLength(200)]
        public string? ClientName { get; set; }

        /// <summary>JSON array of the exact redirect URIs registered.</summary>
        [Required]
        public string RedirectUrisJson { get; set; } = "[]";

        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
    }

    /// <summary>
    /// A one-time authorization code (5 minutes) issued after a SuperAdmin approved the consent
    /// screen. Only the SHA-256 hash is stored; the PKCE challenge is checked at the token endpoint.
    /// </summary>
    public class McpAuthorizationCode
    {
        public long Id { get; set; }

        [Required]
        [StringLength(64)]
        public string CodeHash { get; set; } = string.Empty;

        [Required]
        [StringLength(64)]
        public string ClientId { get; set; } = string.Empty;

        public int UserId { get; set; }

        [Required]
        [StringLength(500)]
        public string RedirectUri { get; set; } = string.Empty;

        [Required]
        [StringLength(128)]
        public string CodeChallenge { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Scope { get; set; }

        public DateTime ExpiresAt { get; set; }
        public DateTime? ConsumedAt { get; set; }

        /// <summary>The grant this code was exchanged for — revoked if the code is ever replayed.</summary>
        public int? GrantId { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// One connected Claude session: what the admin panel lists under Claude Connector with a
    /// Revoke button. Holds the user's <see cref="User.TokenVersion"/> at approval, so "sign out
    /// everywhere", a password change or a role change ends it like any other session.
    /// </summary>
    public class McpGrant
    {
        public int Id { get; set; }

        public int UserId { get; set; }
        public virtual User? User { get; set; }

        [Required]
        [StringLength(64)]
        public string ClientId { get; set; } = string.Empty;

        /// <summary>Snapshot of the client's self-declared name, for the sessions list.</summary>
        [StringLength(200)]
        public string? ClientName { get; set; }

        public int TokenVersion { get; set; }

        [StringLength(200)]
        public string? Scope { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }

        public DateTime? RevokedAt { get; set; }

        [StringLength(100)]
        public string? RevokedReason { get; set; }

        public int? RevokedByUserId { get; set; }

        public virtual ICollection<McpToken> Tokens { get; set; } = new List<McpToken>();
    }

    public static class McpTokenKinds
    {
        public const string Access = "access";
        public const string Refresh = "refresh";
    }

    /// <summary>
    /// An access or refresh token of a <see cref="McpGrant"/>. Opaque random strings; only their
    /// SHA-256 hash is stored, so a database read cannot be replayed as a token. A refresh token is
    /// single-use: <see cref="ConsumedAt"/> is stamped on rotation, and presenting it again revokes
    /// the whole grant (token theft detection).
    /// </summary>
    public class McpToken
    {
        public long Id { get; set; }

        public int GrantId { get; set; }
        public virtual McpGrant? Grant { get; set; }

        [Required]
        [StringLength(10)]
        public string Kind { get; set; } = McpTokenKinds.Access;

        [Required]
        [StringLength(64)]
        public string TokenHash { get; set; } = string.Empty;

        public DateTime ExpiresAt { get; set; }
        public DateTime? ConsumedAt { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// One tool call made through the Claude connector: who, which tool, a short summary of the
    /// arguments (never expense notes, SQL cut to 500 characters), how long and whether it worked.
    /// Kept 180 days (McpMaintenanceService). No foreign keys: the audit outlives a revoked grant.
    /// </summary>
    public class McpToolCallLog
    {
        public long Id { get; set; }

        public int UserId { get; set; }
        public int GrantId { get; set; }

        [Required]
        [StringLength(64)]
        public string Tool { get; set; } = string.Empty;

        [StringLength(1000)]
        public string? ArgumentsSummary { get; set; }

        public int DurationMs { get; set; }
        public bool Succeeded { get; set; }

        [StringLength(500)]
        public string? Error { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
