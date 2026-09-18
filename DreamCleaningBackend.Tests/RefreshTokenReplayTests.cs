using System.Reflection;
using DreamCleaningBackend.Models;
using DreamCleaningBackend.Services;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// A DUPLICATE REFRESH IS A BROWSER RACE, NOT AN ATTACK (2026-09).
    ///
    /// The refresh token is single-use and rotating: every renewal writes a new one and the old
    /// one stops working. That is right against a STOLEN token and wrong against the browser,
    /// which presents the same token twice for reasons nobody chose:
    ///
    ///   - a detail panel fires half a dozen admin GETs at once and they 401 together;
    ///   - the admin has the site open in two tabs, which share one cookie jar;
    ///   - a 60-second background poll lands in the same instant as a click.
    ///
    /// One of those renewals won and the rest were answered "Invalid refresh token" - which the
    /// frontend used to answer by logging the person out, destroying the session that had just
    /// been renewed successfully. That is the intermittent auto-logout.
    ///
    /// So the token a renewal REPLACED stays acceptable for a short grace window. Inside it a
    /// duplicate presentation rotates NOTHING and hands back the tokens the winning call already
    /// issued, so every racing caller converges on one current session. Outside it the token is
    /// dead, which is the single-use property that actually matters.
    /// </summary>
    public class RefreshTokenReplayTests
    {
        [Fact]
        public void TheTokenAReplacedRenewalHandedBackIsStillAcceptedForAMoment()
        {
            var user = Renewed(replacedAgo: TimeSpan.FromSeconds(2));

            Assert.True(IsWithinGrace(user, "the-token-that-was-replaced"));
        }

        [Fact]
        public void OnceTheWindowClosesTheOldTokenIsDead()
        {
            // Past the window this is no longer a race - it is a token that has been out of use
            // for longer than any burst of requests lasts, and it stops working.
            var user = Renewed(replacedAgo: AuthService.RefreshTokenReplayGrace + TimeSpan.FromSeconds(1));

            Assert.False(IsWithinGrace(user, "the-token-that-was-replaced"));
        }

        [Fact]
        public void SomeOtherTokenIsNeverAccepted()
        {
            var user = Renewed(replacedAgo: TimeSpan.FromSeconds(2));

            Assert.False(IsWithinGrace(user, "a-token-from-somewhere-else"));
        }

        [Fact]
        public void AReplayCannotResurrectASessionWhoseCurrentTokenIsGone()
        {
            // A revoke drops RefreshToken (and the window with it). Even if a stale window
            // survived somehow, there is no current session to hand back, so nothing is returned:
            // the grace window widens no privilege, it only points a racing caller at a session
            // that already exists.
            var user = Renewed(replacedAgo: TimeSpan.FromSeconds(2));
            user.RefreshToken = null;

            Assert.False(IsWithinGrace(user, "the-token-that-was-replaced"));
        }

        [Fact]
        public void AnExpiredCurrentTokenIsNotRescuedByTheWindowEither()
        {
            var user = Renewed(replacedAgo: TimeSpan.FromSeconds(2));
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddMinutes(-1);

            Assert.False(IsWithinGrace(user, "the-token-that-was-replaced"));
        }

        [Fact]
        public void AnAccountThatHasNeverRenewedHasNoWindowOpen()
        {
            var user = new User
            {
                Id = 7,
                RefreshToken = "the-only-token-so-far",
                RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30)
            };

            Assert.False(IsWithinGrace(user, "the-only-token-so-far"));
            Assert.False(IsWithinGrace(user, ""));
        }

        [Fact]
        public void TheWindowIsShortEnoughToOnlyCoverARace()
        {
            // Not a policy knob. It covers a burst of parallel 401s and a second tab; anything
            // approaching a session length would make the old token a second credential.
            Assert.True(AuthService.RefreshTokenReplayGrace <= TimeSpan.FromMinutes(2));
            Assert.True(AuthService.RefreshTokenReplayGrace >= TimeSpan.FromSeconds(10));
        }

        // -- helpers --------------------------------------------------------------------------

        private static User Renewed(TimeSpan replacedAgo) => new()
        {
            Id = 7,
            RefreshToken = "the-token-issued-by-the-winning-call",
            RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(30),
            PreviousRefreshToken = "the-token-that-was-replaced",
            PreviousRefreshTokenExpiryTime =
                DateTime.UtcNow.Add(AuthService.RefreshTokenReplayGrace - replacedAgo)
        };

        /// <summary>
        /// The rule itself is private to AuthService because nothing outside the refresh path may
        /// decide it; reflection keeps the test on the real implementation rather than a copy.
        /// </summary>
        private static bool IsWithinGrace(User user, string suppliedRefreshToken)
        {
            var method = typeof(AuthService).GetMethod(
                "IsWithinRefreshReplayGrace", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            return (bool)method!.Invoke(null, new object[] { user, suppliedRefreshToken })!;
        }
    }
}
