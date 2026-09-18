using System;
using System.IO;
using System.Linq;
using DreamCleaningBackend.Helpers;
using DreamCleaningBackend.Models;
using Xunit;

namespace DreamCleaningBackend.Tests
{
    /// <summary>
    /// THE WELCOME EMAIL, AND WHO ACTUALLY GETS ONE.
    ///
    /// The mail used to be sent from exactly one place — the end of local email verification — so
    /// a customer who signed up with Google or Apple never received one: those accounts arrive
    /// already verified (the provider vouches for the address) and so never reach that path at
    /// all. <see cref="WelcomeEmailPolicy"/> now states the rule once and the four paths that can
    /// FIRST give an account a usable address all resolve through it.
    ///
    /// What must stay true:
    ///   • a Google or Apple sign-up is welcomed, and the mail says how they signed in rather than
    ///     claiming they verified an email they never verified,
    ///   • an Apple "Hide My Email" relay address is NEVER mailed — it forwards only when the
    ///     sending address is registered with Apple, so it is as likely to bounce as to arrive —
    ///     and that account is welcomed when it supplies a real address instead,
    ///   • exactly one per account, claimed on a column rather than inferred from a timestamp:
    ///     Apple's "new user" test is <c>CreatedAt &gt; now − 1 minute</c>, which a retried sign-in
    ///     inside that minute passes twice,
    ///   • no-email placeholder accounts, blocked accounts and non-customers are never mailed.
    /// </summary>
    public class WelcomeEmailTests
    {
        private static string BackendRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "DreamCleaningBackend")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "DreamCleaningBackend");
        }

        private static string ReadSource(params string[] relativeParts)
        {
            var path = Path.Combine(new[] { BackendRoot() }.Concat(relativeParts).ToArray());
            Assert.True(File.Exists(path), $"Expected source file not found: {path}");
            return File.ReadAllText(path);
        }

        private static User Account(
            string email = "customer@example.com",
            string authProvider = "Local",
            UserRole role = UserRole.Customer,
            bool isActive = true,
            bool isDeleted = false,
            bool isNoEmailUser = false,
            DateTime? welcomeEmailSentAt = null) => new()
            {
                Id = 42,
                Email = email,
                FirstName = "Ann",
                LastName = "Lee",
                AuthProvider = authProvider,
                Role = role,
                IsActive = isActive,
                IsDeleted = isDeleted,
                IsNoEmailUser = isNoEmailUser,
                WelcomeEmailSentAt = welcomeEmailSentAt
            };

        [Fact]
        public void GoogleSignUp_IsWelcomed()
        {
            var user = Account(authProvider: "Google");

            Assert.True(WelcomeEmailPolicy.ShouldSend(user));
            Assert.Equal("customer@example.com", WelcomeEmailPolicy.ResolveRecipient(user));
            Assert.Equal("Google", WelcomeEmailPolicy.ResolveProviderLabel(user));
        }

        [Fact]
        public void AppleSignUpOnASharedAddress_IsWelcomed()
        {
            var user = Account(email: "real@icloud.com", authProvider: "Apple");

            Assert.True(WelcomeEmailPolicy.ShouldSend(user));
            Assert.Equal("Apple", WelcomeEmailPolicy.ResolveProviderLabel(user));
        }

        [Fact]
        public void AppleRelayAddress_IsNeverMailed()
        {
            // "Hide My Email". The relay only forwards when the sending address is registered with
            // Apple for the private email relay service, so this is as likely to bounce as to
            // arrive — and the account is blocked from the API until it gives a real address
            // anyway. It gets its welcome then, at an address we know works.
            var user = Account(email: "abc123def@privaterelay.appleid.com", authProvider: "Apple");

            Assert.Null(WelcomeEmailPolicy.ResolveRecipient(user));
            Assert.False(WelcomeEmailPolicy.ShouldSend(user));
        }

        [Fact]
        public void TheSameAccountIsWelcomedOnceItSuppliesARealAddress()
        {
            // The exact Apple relay account from the test above, after VerifyRealEmailCode.
            var user = Account(email: "real@gmail.com", authProvider: "Apple");

            Assert.True(WelcomeEmailPolicy.ShouldSend(user));
        }

        [Fact]
        public void AnAlreadyWelcomedAccountIsNeverWelcomedAgain()
        {
            // The claim is the column, not a "was this created in the last minute" guess — which
            // is what a retried Apple sign-in inside that minute would pass twice.
            var user = Account(
                authProvider: "Apple",
                welcomeEmailSentAt: new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc));

            Assert.False(WelcomeEmailPolicy.ShouldSend(user));
        }

        [Fact]
        public void ANoEmailCustomerIsNeverMailed()
        {
            var user = Account(email: NoEmailHelper.GeneratePlaceholder(), isNoEmailUser: true);

            Assert.Null(WelcomeEmailPolicy.ResolveRecipient(user));
            Assert.False(WelcomeEmailPolicy.ShouldSend(user));
        }

        [Fact]
        public void BlockedAndDeletedAccountsAreNeverMailed()
        {
            Assert.False(WelcomeEmailPolicy.ShouldSend(Account(authProvider: "Google", isActive: false)));
            Assert.False(WelcomeEmailPolicy.ShouldSend(Account(authProvider: "Google", isDeleted: true)));
        }

        [Fact]
        public void OnlyCustomersGetTheCustomerWelcome()
        {
            // The copy is about booking cleanings and managing apartments. A Google sign-up whose
            // address matches a cleaner record becomes a Cleaner login account, and staff have
            // their own mail (SendAdminWelcomeEmailAsync) sent when the account is created.
            foreach (var role in new[] { UserRole.Cleaner, UserRole.Admin, UserRole.SuperAdmin, UserRole.Moderator })
                Assert.False(WelcomeEmailPolicy.ShouldSend(Account(authProvider: "Google", role: role)));
        }

        [Fact]
        public void ALocalAccountThatMerelyLinkedGoogleIsNotToldItSignedUpWithIt()
        {
            // Linking leaves AuthProvider "Local", and the label is read off the account rather
            // than passed in by whichever endpoint happens to be running.
            Assert.Null(WelcomeEmailPolicy.ResolveProviderLabel(Account()));
            Assert.Null(WelcomeEmailPolicy.ResolveProviderLabel(null));
        }

        [Fact]
        public void EverySendingPathRoutesThroughTheSharedHelper()
        {
            // Source-level, because what is being prevented is a future path growing its own
            // answer. The only caller of the mail itself is TrySendWelcomeEmailAsync; the four
            // paths that can first give an account a usable address call that.
            var source = ReadSource("Services", "AuthService.cs");

            Assert.Contains("WelcomeEmailPolicy.ShouldSend", source);
            Assert.DoesNotContain("_emailService.SendWelcomeEmailAsync", source);
            Assert.Equal(1, CountOccurrences(source, ".SendWelcomeEmailAsync(recipient"));
            Assert.Equal(4, CountOccurrences(source, "TrySendWelcomeEmailAsync(user)"));

            // And it is claimed before it is sent, so two racing sign-ins cannot both mail.
            Assert.Contains("WelcomeEmailSentAt == null", source);
            Assert.Contains("ExecuteUpdateAsync", source);

            // Detached with its own DI scope — EmailService is scoped and shares this service's
            // DbContext. See Helpers/BackgroundWork.
            Assert.Contains("BackgroundWork.Run(_scopeFactory, _logger, $\"welcome email for", source);
        }

        [Fact]
        public void ASocialSignUpIsNotToldItVerifiedAnEmail()
        {
            // "Your email has been verified successfully" describes a step a Google or Apple
            // customer never took.
            var source = ReadSource("Services", "EmailService.cs");
            var welcome = source.Substring(source.IndexOf("public async Task SendWelcomeEmailAsync", StringComparison.Ordinal));

            Assert.Contains("string? authProvider = null", welcome);
            Assert.Contains("you signed in with {authProvider}", welcome);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }
    }
}
