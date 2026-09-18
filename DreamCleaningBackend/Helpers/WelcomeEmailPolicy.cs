using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "does this account get the welcome email, and at what address?".
    ///
    /// WHY IT EXISTS (2026-09). The welcome mail used to be sent from exactly one place — the end
    /// of local email verification — so a customer who signed up with Google or Apple never
    /// received one at all: those accounts arrive already verified (the provider vouches for the
    /// address) and therefore never pass through that path. The rule is now stated once and every
    /// path that can FIRST give an account a usable address resolves through it: Google sign-up,
    /// Apple sign-up, local email verification, and the Apple relay account finally supplying a
    /// real address.
    ///
    /// What it encodes:
    ///   * ONCE PER ACCOUNT. <see cref="User.WelcomeEmailSentAt"/> is the claim, not a heuristic.
    ///     Apple's "is this a new user" test is <c>CreatedAt &gt; now - 1 minute</c>, so a retried
    ///     sign-in inside that minute would otherwise mail them twice; and a relay account that is
    ///     welcomed when it supplies a real address must not be welcomed a second time.
    ///   * NEVER TO AN APPLE RELAY ADDRESS. A @privaterelay.appleid.com address only forwards when
    ///     the sending address is registered with Apple for the private email relay service, so
    ///     mail to one is as likely to bounce as to arrive. Those accounts are already blocked
    ///     from the API until they give a real email (the RequiresRealEmail middleware) — they are
    ///     welcomed then, at an address we know works.
    ///   * NEVER TO A NO-EMAIL PLACEHOLDER (see <see cref="NoEmailHelper"/>), and never to a
    ///     blocked or soft-deleted account.
    ///   * CUSTOMERS ONLY. The copy is about booking cleanings and managing apartments. A cleaner's
    ///     login account (a Google sign-up whose address matches a cleaner record becomes one) and
    ///     staff accounts are not that audience — staff have their own mail, sent by
    ///     AdminUsersController when the account is created.
    /// </summary>
    public static class WelcomeEmailPolicy
    {
        /// <summary>Apple's "Hide My Email" relay domain.</summary>
        public const string AppleRelayDomain = "@privaterelay.appleid.com";

        /// <summary>True for an Apple "Hide My Email" relay address.</summary>
        public static bool IsAppleRelayAddress(string? email) =>
            !string.IsNullOrWhiteSpace(email)
            && email.TrimEnd().EndsWith(AppleRelayDomain, StringComparison.OrdinalIgnoreCase);

        /// <summary>The address a welcome email would actually reach, or null when the account has
        /// none we are willing to mail (no email on file, a placeholder, or an Apple relay).</summary>
        public static string? ResolveRecipient(User? user)
        {
            var email = NoEmailHelper.ResolveRealEmail(user);
            return IsAppleRelayAddress(email) ? null : email;
        }

        /// <summary>How the mail names the way they signed up ("Google" / "Apple"), or null for an
        /// ordinary email-and-password account. Read off the account rather than passed in by the
        /// caller, so a local account that merely LINKED Google is not told it signed up with it.</summary>
        public static string? ResolveProviderLabel(User? user) => user?.AuthProvider switch
        {
            "Google" => "Google",
            "Apple" => "Apple",
            _ => null
        };

        /// <summary>True when this account should be welcomed right now.</summary>
        public static bool ShouldSend(User? user)
        {
            if (user == null) return false;
            if (user.WelcomeEmailSentAt != null) return false;
            if (!user.IsActive || user.IsDeleted) return false;
            if (user.Role != UserRole.Customer) return false;
            return ResolveRecipient(user) != null;
        }
    }
}
