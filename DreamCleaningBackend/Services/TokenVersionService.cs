using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DreamCleaningBackend.Services
{
    /// <summary>
    /// Checks a JWT's "tv" claim against the account's current <see cref="User.TokenVersion"/> on
    /// every authenticated request (wired into JwtBearerEvents.OnTokenValidated in Program.cs).
    ///
    /// Singleton + IMemoryCache because this runs on EVERY request and a role change is rare: the
    /// version is cached per user for <see cref="CacheTtlSeconds"/> seconds, and a revoke writes
    /// the new value straight into the cache rather than waiting for the entry to age out.
    /// </summary>
    public class TokenVersionService : ITokenVersionService
    {
        /// <summary>The JWT claim carrying the version the token was minted at.</summary>
        public const string ClaimType = "tv";

        /// <summary>
        /// Marks the 401 a REVOKED session produces, so the frontend can end its own session on
        /// that and only that. Without it the browser would have to read every 401 as "you have
        /// been signed out", which is wrong the moment one endpoint 401s for its own reasons
        /// (a bad guest payment token, say) while a perfectly good admin is signed in.
        /// </summary>
        public const string RevokedHeader = "X-Session-Revoked";

        // Short on purpose. The revoke path updates the cache itself, so this only covers the
        // narrow race where another request reloads the version mid-save; a minute of staleness
        // there is the whole exposure.
        private const int CacheTtlSeconds = 60;

        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TokenVersionService> _logger;

        public TokenVersionService(IMemoryCache cache, IServiceScopeFactory scopeFactory, ILogger<TokenVersionService> logger)
        {
            _cache = cache;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        private static string Key(int userId) => $"TokenVersion:{userId}";

        public async Task<bool> IsTokenCurrentAsync(int userId, int tokenVersion)
        {
            if (_cache.TryGetValue<int?>(Key(userId), out var cached))
                return cached.HasValue && cached.Value == tokenVersion;

            int? current;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                current = await context.Users
                    .Where(u => u.Id == userId)
                    .Select(u => (int?)u.TokenVersion)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex)
            {
                // Fail OPEN on a database blip. Every authenticated request would fail anyway once
                // it reaches its own query, and signing the entire customer base out because MySQL
                // hiccuped for a second is the worse of the two failures.
                _logger.LogError(ex, "Token version lookup failed for user {UserId}; accepting the token", userId);
                return true;
            }

            _cache.Set(Key(userId), current, TimeSpan.FromSeconds(CacheTtlSeconds));

            // No row = the account is gone. Its tokens stop working, which is the point.
            return current.HasValue && current.Value == tokenVersion;
        }

        public void RevokeSessions(User user)
        {
            user.TokenVersion++;
            user.RefreshToken = null;
            user.RefreshTokenExpiryTime = null;
            // The replay window (User.PreviousRefreshToken) exists so a browser racing itself is
            // not thrown out. It must not survive a revoke: leaving it open would let the very
            // session being ended mint a replacement for up to a minute afterwards, which is the
            // one thing this method is for.
            user.PreviousRefreshToken = null;
            user.PreviousRefreshTokenExpiryTime = null;
        }

        public void SyncCache(int userId, int tokenVersion)
        {
            _cache.Set(Key(userId), (int?)tokenVersion, TimeSpan.FromSeconds(CacheTtlSeconds));
        }
    }
}
