using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// GUEST CHECKOUT MUST NOT BE A WAY INTO SOMEBODY ELSE'S ACCOUNT.
    ///
    /// <c>POST api/booking/prepare-payment</c> is [AllowAnonymous] and signs the caller in as the
    /// account matching the contact email, so the matching rule decides who can be impersonated by
    /// typing an address into a booking form. It used to match on <c>!IsDeleted</c> alone, which
    /// handed out a signed 30-day token — carrying the matched account's real Role claim — for any
    /// email somebody could guess, an admin's included.
    ///
    /// The trap these tests exist to keep shut is that "passwordless" is NOT the test. Google and
    /// Apple accounts have no password either, so a <c>PasswordHash == null</c> check alone leaves
    /// every social account claimable. See <see cref="GuestAccountClaimPolicy"/>.
    /// </summary>
    public class GuestAccountClaimPolicyTests
    {
        private static User GuestAccount() => new User
        {
            Id = 1,
            Email = "diana@example.com",
            AuthProvider = "Local",
            PasswordHash = null,
            PasswordSalt = null,
            Role = UserRole.Customer,
            IsActive = true,
            IsDeleted = false
        };

        // ── The flows that must keep working ──────────────────────────────────────────────

        [Fact]
        public void ARepeatGuestBookingFindsItsOwnPasswordlessAccount()
        {
            // The account this same path created on the customer's first booking. Refusing it
            // would strand a returning guest: they have no password to sign in with, and their
            // order history lives on that row.
            Assert.True(GuestAccountClaimPolicy.MayClaimWithoutCredentials(GuestAccount()));
        }

        [Fact]
        public void AnAdminCreatedCustomerCanStillBookOnline()
        {
            // AdminUsersController's register endpoint: AuthProvider "Admin", no password — the
            // no-email / phone-booking customers. They must be able to book online later and land
            // on the cleanings the office entered for them.
            var user = GuestAccount();
            user.AuthProvider = "Admin";

            Assert.True(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Fact]
        public void ALegacyRowWithNoProviderReadsAsLocal()
        {
            // The column is nullable and predates being populated everywhere. Treating null as
            // "unknown, therefore refuse" would lock the oldest guest accounts out of rebooking.
            var user = GuestAccount();
            user.AuthProvider = null;

            Assert.True(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Theory]
        [InlineData("local")]
        [InlineData("LOCAL")]
        [InlineData("admin")]
        public void TheProviderIsMatchedCaseInsensitively(string provider)
        {
            var user = GuestAccount();
            user.AuthProvider = provider;

            Assert.True(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        // ── The takeover attempts ─────────────────────────────────────────────────────────

        [Fact]
        public void ARegisteredAccountWithAPasswordIsRefused()
        {
            var user = GuestAccount();
            user.PasswordHash = "hash";
            user.PasswordSalt = "salt";

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Theory]
        [InlineData("Google")]
        [InlineData("Apple")]
        public void ASocialAccountIsRefused_EvenThoughItHasNoPassword(string provider)
        {
            // THE regression this file exists for. Both are created with PasswordHash null, so a
            // bare "has no password" test would hand them over. The external identity is the
            // credential, and this path cannot check it.
            var user = GuestAccount();
            user.AuthProvider = provider;
            user.PasswordHash = null;

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Fact]
        public void ALocalAccountWithALinkedSocialIdentityIsRefused()
        {
            // AuthService's Google branch links an external id onto an EXISTING row without
            // necessarily rewriting AuthProvider, so the provider string can still read "Local"
            // while Google is a live way in.
            var user = GuestAccount();
            user.ExternalAuthId = "Google:12345";

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Theory]
        [InlineData(UserRole.Admin)]
        [InlineData(UserRole.SuperAdmin)]
        [InlineData(UserRole.Moderator)]
        [InlineData(UserRole.Cleaner)]
        public void AStaffAccountIsRefused_EvenIfItHasNoPassword(UserRole role)
        {
            // The role is checked rather than assumed. The admin register endpoint hardcodes
            // Customer, but the column is mutable afterwards — a promotion must not make an
            // account claimable as a side effect nobody connected to this endpoint.
            var user = GuestAccount();
            user.Role = role;

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Fact]
        public void ABlockedAccountIsRefused()
        {
            // Blocking an account has to mean it is unreachable, including by this door.
            var user = GuestAccount();
            user.IsActive = false;

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Fact]
        public void ADeletedAccountIsRefused()
        {
            var user = GuestAccount();
            user.IsDeleted = true;

            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(user));
        }

        [Fact]
        public void NoUserIsRefused()
        {
            // A brand-new guest (no match at all) never reaches this rule — the caller creates
            // the account instead. Asserted so a future caller that DOES pass null cannot read
            // "nobody" as "allowed".
            Assert.False(GuestAccountClaimPolicy.MayClaimWithoutCredentials(null));
        }

        // ── The refusal has to be actionable ──────────────────────────────────────────────

        [Fact]
        public void TheRefusalTellsTheCustomerWhatToDo()
        {
            // The customer is standing on the payment step. "Invalid request" strands them; the
            // message has to name the cause and the way forward.
            Assert.Contains("already exists", GuestAccountClaimPolicy.LoginRequiredMessage);
            Assert.Contains("sign in", GuestAccountClaimPolicy.LoginRequiredMessage);
        }
    }
}
