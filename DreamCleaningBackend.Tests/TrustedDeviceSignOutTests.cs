using DreamCleaningBackend.Data;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// REMOVING A TRUSTED DEVICE MUST SIGN IT OUT, ONLINE OR NOT (2026-09).
    ///
    /// "Remove" on the profile's Trusted Devices list stamped TrustedDevice.RevokedAt and nothing
    /// else. That row only decides whether the NEXT login from the device skips 2FA; the session
    /// the device already held is a self-contained 30-day JWT that never looked at it. So a
    /// SuperAdmin removed a relative's login from their profile and the relative stayed in the
    /// admin panel. Password change and reset had the same hole: they untrusted every device and
    /// left every signed-in browser signed in on the old password.
    ///
    /// The fix reuses the account-wide session version (User.TokenVersion, see
    /// SessionRevocationTests): end every session, re-issue the caller's. There is one version and
    /// one refresh token per account, so one device cannot be singled out - the owner's other
    /// devices sign in again with their password (still trusted, so no 2FA challenge).
    /// </summary>
    public class TrustedDeviceSignOutTests
    {
        // -- TwoFactorService ----------------------------------------------------------------

        [Fact]
        public async Task SigningOutOtherDevices_UntrustsEveryDeviceExceptThisOne()
        {
            // Somebody who still knows the password could otherwise sign straight back in from a
            // trusted browser without being challenged.
            var (service, context) = NewService();
            context.TrustedDevices.AddRange(Device(1, userId: 7), Device(2, userId: 7), Device(3, userId: 7), Device(4, userId: 8));
            await context.SaveChangesAsync();

            await service.RevokeAllTrustedDevicesExceptAsync(7, new[] { 2 });

            var rows = await context.TrustedDevices.AsNoTracking().ToDictionaryAsync(d => d.Id);
            Assert.NotNull(rows[1].RevokedAt);
            Assert.Null(rows[2].RevokedAt);   // the device making the request
            Assert.NotNull(rows[3].RevokedAt);
            Assert.Null(rows[4].RevokedAt);   // somebody else's account is untouched
        }

        [Fact]
        public async Task RemovingADeviceThatIsNotYours_ReportsNotFound()
        {
            // The controller ends sessions only after a real removal. A device id belonging to
            // another account must not become a way to sign yourself out for nothing - nor
            // touch their row.
            var (service, context) = NewService();
            context.TrustedDevices.Add(Device(1, userId: 8));
            await context.SaveChangesAsync();

            Assert.False(await service.RevokeTrustedDeviceAsync(7, 1));
            Assert.Null((await context.TrustedDevices.AsNoTracking().SingleAsync()).RevokedAt);
        }

        [Fact]
        public async Task RemovingYourOwnDevice_RevokesIt()
        {
            var (service, context) = NewService();
            context.TrustedDevices.Add(Device(1, userId: 7));
            await context.SaveChangesAsync();

            Assert.True(await service.RevokeTrustedDeviceAsync(7, 1));
            Assert.NotNull((await context.TrustedDevices.AsNoTracking().SingleAsync()).RevokedAt);
        }

        // -- AuthController wiring -----------------------------------------------------------

        [Fact]
        public void RemovingAnotherDevice_EndsTheOtherSessions()
        {
            var method = Method(Controller(), "public async Task<ActionResult> RevokeTrustedDevice(int id)");

            Assert.Contains("_twoFactorService.RevokeTrustedDeviceAsync(userId, id)", method);
            Assert.Contains("EndOtherSessionsAsync(userId)", method);

            // Removing the device you are ON is "ask me for 2FA here next time" - it must not
            // sign out the owner's phone as a side effect.
            Assert.Contains("if (currentDeviceIds.Contains(id))", method);
            Assert.True(
                method.IndexOf("currentDeviceIds.Contains(id)", StringComparison.Ordinal)
                < method.IndexOf("EndOtherSessionsAsync(userId)", StringComparison.Ordinal));
        }

        [Fact]
        public void SignOutOtherSessions_ExistsAndEndsTheOtherSessions()
        {
            // A device signed in WITHOUT "trust this device" never appears in the list, so without
            // this endpoint there was no way to end its session at all.
            var source = Controller();
            Assert.Contains("[HttpPost(\"sign-out-other-sessions\")]", source);

            var method = Method(source, "public async Task<ActionResult> SignOutOtherSessions()");
            Assert.Contains("RevokeAllTrustedDevicesExceptAsync(userId, currentDeviceIds)", method);
            Assert.Contains("EndOtherSessionsAsync(userId)", method);
        }

        [Fact]
        public void EndingSessions_RevokesSavesAndSyncsTheCache_ThenReissuesTheCaller()
        {
            // Order is the whole rule. SyncCache before the save lets a racing request re-cache the
            // old version; a replacement minted before the revoke carries the OLD version and is
            // refused along with everybody else's, signing the owner out too.
            var source = Controller();
            var endAll = Method(source, "private async Task EndAllSessionsAsync(int userId, bool notify = true)");
            AssertInOrder(endAll,
                "_tokenVersions.RevokeSessions(user)",
                "await _dbContext.SaveChangesAsync()",
                "_tokenVersions.SyncCache(user.Id, user.TokenVersion)");

            var endOthers = Method(source, "private async Task<AuthResponseDto> EndOtherSessionsAsync(int userId)");
            AssertInOrder(endOthers,
                "await EndAllSessionsAsync(userId, notify: false)",
                "await _authService.RefreshUserToken(userId)",
                "SetAuthCookies(reissued.Token, reissued.RefreshToken)",
                // The SignalR notice goes out last: a browser that re-checks its session before the
                // caller has been re-issued one would be checking a moment too early.
                "await NotifySessionsEndedAsync(userId)");
        }

        [Fact]
        public void ChangingThePassword_EndsTheOtherSessions()
        {
            var method = Method(Controller(), "public async Task<ActionResult> ChangePassword(ChangePasswordDto changePasswordDto)");
            Assert.Contains("EndOtherSessionsAsync(userId)", method);
        }

        [Fact]
        public void ResettingThePassword_EndsEverySession()
        {
            // Whoever resets is not signed in here, so nothing is re-issued.
            var method = Method(Controller(), "public async Task<ActionResult> ResetPassword(ResetPasswordDto resetDto)");
            Assert.Contains("await EndAllSessionsAsync(userId)", method);
            Assert.DoesNotContain("EndOtherSessionsAsync", method);
        }

        // -- helpers --------------------------------------------------------------------------

        private static (TwoFactorService, ApplicationDbContext) NewService()
        {
            var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"trusted-device-signout-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            // The email service is only reached by the challenge flow, never by revocation.
            return (new TwoFactorService(context, null!, NullLogger<TwoFactorService>.Instance), context);
        }

        private static TrustedDevice Device(int id, int userId) => new()
        {
            Id = id,
            UserId = userId,
            TokenHash = $"hash-{id}",
            CreatedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow
        };

        private static string Controller() =>
            ReadBackendFile(Path.Combine("Controllers", "AuthController.cs")).Replace("\r\n", "\n");

        /// <summary>The method's text, from its signature to the next member's attribute or signature.</summary>
        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"'{signature}' was renamed or removed.");

            var body = source.Substring(start + signature.Length);
            var ends = new[] { "\n        [Http", "\n        public ", "\n        private ", "\n        // " }
                .Select(marker => body.IndexOf(marker, StringComparison.Ordinal))
                .Where(i => i >= 0)
                .DefaultIfEmpty(body.Length)
                .Min();
            return signature + body.Substring(0, ends);
        }

        private static void AssertInOrder(string text, params string[] parts)
        {
            var at = -1;
            foreach (var part in parts)
            {
                var next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
                Assert.True(next > at, $"Expected '{part}' after the previous step.");
                at = next;
            }
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
