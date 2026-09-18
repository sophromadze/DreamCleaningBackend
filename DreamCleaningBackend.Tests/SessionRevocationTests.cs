using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// CHANGING SOMEBODY'S ROLE MUST LOG THEM OUT WHETHER THEY ARE ONLINE OR NOT (2026-09).
    ///
    /// A JWT is self-contained: it is signed for 30 days and carries the role it was minted with,
    /// and nothing on the server re-read the account afterwards. So the ONLY thing ending a
    /// session on a role change was the SignalR "RoleChanged" notice - which reaches a browser
    /// that has the page open, and nothing else. An admin demoting somebody who happened to be
    /// offline changed a database row and nothing more: that person came back holding a token
    /// that still said "Admin", for up to a month.
    ///
    /// The fix is a version counter on the account (User.TokenVersion), stamped into every token
    /// as the "tv" claim and re-checked on every authenticated request. Two halves, both needed:
    ///
    ///   - bumping the version refuses the ACCESS token the browser already holds, and
    ///   - dropping the refresh token stops that browser silently minting a replacement and
    ///     staying signed in.
    ///
    /// Legacy tokens carry no claim at all and read as 0, which is what the column defaults to -
    /// so shipping this does not sign the entire customer base out on deploy.
    /// </summary>
    public class SessionRevocationTests
    {
        // -- The revoke itself ---------------------------------------------------------------

        [Fact]
        public void RevokingSessions_BumpsTheVersionAndDropsTheRefreshToken()
        {
            var user = new User
            {
                Id = 7,
                TokenVersion = 3,
                RefreshToken = "still-valid-for-30-days",
                RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30),
                PreviousRefreshToken = "replaced-a-moment-ago",
                PreviousRefreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(60)
            };

            NewService(out _).RevokeSessions(user);

            Assert.Equal(4, user.TokenVersion);

            // Without this half the browser answers the 401 with a refresh and carries straight
            // on - a new token, the new role, never a login screen.
            Assert.Null(user.RefreshToken);
            Assert.Null(user.RefreshTokenExpiryTime);

            // And the replay window with it. It exists so a browser racing itself is not thrown
            // out (User.PreviousRefreshToken); left open across a revoke it would let the session
            // being ended mint a replacement for up to a minute afterwards.
            Assert.Null(user.PreviousRefreshToken);
            Assert.Null(user.PreviousRefreshTokenExpiryTime);
        }

        [Fact]
        public void NeitherRolePathRevokesWhenTheRoleDidNotActuallyMove()
        {
            // The dedicated role endpoint used to revoke on EVERY call, and the Cleaners tab calls
            // it with a fixed role ("Make a cleaner" / "Move to Customer") - so a second click, a
            // double submit, or an admin re-confirming the role somebody already had threw that
            // person out of their session for nothing. Both writers of User.Role now gate on the
            // same `roleChanged`.
            // Newlines normalised so the assertion says what it means on any checkout.
            var source = ReadBackendFile(Path.Combine("Controllers", "Admin", "AdminUsersController.cs"))
                .Replace("\r\n", "\n");

            Assert.Equal(2, Occurrences(source, "var roleChanged = targetUser.Role != newRole;"));
            Assert.Equal(2, Occurrences(source, "if (roleChanged)\n                _tokenVersions.RevokeSessions(targetUser);"));
            Assert.Equal(2, Occurrences(source, "if (roleChanged)\n                _tokenVersions.SyncCache(targetUser.Id, targetUser.TokenVersion);"));

            // A bare, ungated call (the revoke sitting at statement level, not under the gate)
            // would put the original bug straight back.
            Assert.Equal(0, Occurrences(source, "\n            _tokenVersions.RevokeSessions(targetUser);"));
        }

        // -- What the per-request check accepts and refuses -----------------------------------

        [Fact]
        public async Task ATokenMintedBeforeTheRevokeIsRefused()
        {
            var service = NewService(out var context);
            context.Users.Add(NewUser(id: 7, tokenVersion: 1));
            await context.SaveChangesAsync();

            // The token in the offline user's browser still says version 0.
            Assert.False(await service.IsTokenCurrentAsync(7, 0));
        }

        [Fact]
        public async Task ATokenMintedAfterTheRevokeIsAccepted()
        {
            var service = NewService(out var context);
            context.Users.Add(NewUser(id: 7, tokenVersion: 1));
            await context.SaveChangesAsync();

            Assert.True(await service.IsTokenCurrentAsync(7, 1));
        }

        [Fact]
        public async Task ATokenFromBeforeThisFeatureCarriesNoClaimAndKeepsWorking()
        {
            // Program.cs reads a missing "tv" claim as 0; the column defaults to 0. Every session
            // open at deploy time therefore survives the deploy, which is the point of pairing
            // those two defaults.
            var service = NewService(out var context);
            context.Users.Add(NewUser(id: 7, tokenVersion: 0));
            await context.SaveChangesAsync();

            Assert.True(await service.IsTokenCurrentAsync(7, 0));
        }

        [Fact]
        public async Task ADeletedAccountsTokenIsRefused()
        {
            // No row to compare against. A hard-deleted user's signed token would otherwise keep
            // working until it expired.
            var service = NewService(out _);

            Assert.False(await service.IsTokenCurrentAsync(999, 0));
        }

        [Fact]
        public async Task TheNewVersionIsWhatTheNextRequestReads()
        {
            // The check is cached per user, so a revoke that only wrote the database would be
            // invisible for as long as the cache entry lived. The revoke path publishes the new
            // value itself - SyncCache, called AFTER the save.
            var service = NewService(out var context);
            var user = NewUser(id: 7, tokenVersion: 0);
            context.Users.Add(user);
            await context.SaveChangesAsync();

            Assert.True(await service.IsTokenCurrentAsync(7, 0));   // populates the cache

            service.RevokeSessions(user);
            await context.SaveChangesAsync();
            service.SyncCache(user.Id, user.TokenVersion);

            Assert.False(await service.IsTokenCurrentAsync(7, 0));
            Assert.True(await service.IsTokenCurrentAsync(7, 1));
        }

        // -- The mint side --------------------------------------------------------------------

        [Fact]
        public void EveryIssuedTokenCarriesTheAccountsCurrentVersion()
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(
                CreateToken(NewUser(id: 7, tokenVersion: 9)));

            var claim = token.Claims.FirstOrDefault(c => c.Type == TokenVersionService.ClaimType);

            Assert.NotNull(claim);
            Assert.Equal("9", claim!.Value);
        }

        // -- The call sites -------------------------------------------------------------------

        [Fact]
        public void BothAdminRoleChangePathsRevokeTheAccountsSessions()
        {
            // Two surfaces move a role: the dedicated role endpoint, and the Users-tab edit form
            // that carries a Role field alongside everything else. Missing either one leaves the
            // original bug in place on that path only, which is the hard version to spot.
            var source = ReadBackendFile(Path.Combine("Controllers", "Admin", "AdminUsersController.cs"));

            Assert.Equal(2, Occurrences(source, "_tokenVersions.RevokeSessions(targetUser)"));
            Assert.Equal(2, Occurrences(source, "_tokenVersions.SyncCache(targetUser.Id, targetUser.TokenVersion)"));
        }

        [Fact]
        public void LoggingOutDoesNotRequireAWorkingToken()
        {
            // The moment the browser most needs /auth/logout is straight after a token was
            // refused. While that endpoint required auth, the call 401'd, and the interceptor
            // answers a 401 on an /auth/ URL by calling logout again - a loop. It only clears
            // cookies; it reads no identity.
            var source = ReadBackendFile(Path.Combine("Controllers", "AuthController.cs"));
            var start = source.IndexOf("[HttpPost(\"logout\")]", StringComparison.Ordinal);
            Assert.True(start >= 0, "The logout endpoint was renamed or removed.");

            var rest = source.Substring(start);
            var logout = rest.Substring(0, rest.IndexOf("public ActionResult Logout()", StringComparison.Ordinal));

            Assert.Contains("[AllowAnonymous]", logout);
            Assert.DoesNotContain("[Authorize]", logout);
        }

        // -- helpers --------------------------------------------------------------------------

        private static TokenVersionService NewService(out ApplicationDbContext context)
        {
            var databaseName = $"session-revocation-{Guid.NewGuid()}";

            var provider = new ServiceCollection()
                .AddMemoryCache()
                .AddDbContext<ApplicationDbContext>(o => o
                    .UseInMemoryDatabase(databaseName)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
                .BuildServiceProvider();

            // The service opens its own scope, so the test writes through a second context over
            // the same in-memory store rather than sharing one instance with it.
            context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            return new TokenVersionService(
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<TokenVersionService>.Instance);
        }

        private static User NewUser(int id, int tokenVersion) => new()
        {
            Id = id,
            Email = $"user{id}@example.com",
            FirstName = "Test",
            LastName = "User",
            Role = UserRole.Customer,
            TokenVersion = tokenVersion
        };

        /// <summary>
        /// CreateToken is private and only touches the configured signing key, so none of the
        /// service's other dependencies are reached.
        /// </summary>
        private static string CreateToken(User user)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AppSettings:Token"] = new string('k', 128)
                })
                .Build();

            var service = (AuthService)Activator.CreateInstance(
                typeof(AuthService),
                null, configuration, null, null, null, null, null, null, null, null)!;

            var method = typeof(AuthService).GetMethod("CreateToken", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            return (string)method!.Invoke(service, new object[] { user })!;
        }

        private static int Occurrences(string source, string needle)
        {
            var count = 0;
            for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string ReadBackendFile(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningNG")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            var path = Path.Combine(dir!.FullName, "DreamCleaningBackend", "DreamCleaningBackend", relativePath);
            Assert.True(File.Exists(path), $"{path} was not found.");
            return File.ReadAllText(path);
        }
    }
}
