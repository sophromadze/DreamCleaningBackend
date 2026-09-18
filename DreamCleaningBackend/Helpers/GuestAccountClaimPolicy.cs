using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// WHO MAY BE LOGGED IN BY TYPING THEIR EMAIL ADDRESS INTO A BOOKING FORM.
    ///
    /// <c>POST api/booking/prepare-payment</c> is [AllowAnonymous] and auto-creates an account
    /// from the booking's contact details so a guest can check out without registering
    /// (<c>AuthService.CreateOrGetGuestUserAsync</c>). When the address already belongs to
    /// somebody, that path used to hand the caller a session for the matched account with no
    /// test beyond <c>!IsDeleted</c> — so an unauthenticated POST naming any customer's, or any
    /// ADMIN's, email address returned a signed 30-day token carrying that account's real Role
    /// claim, plus (in cookie-auth mode) the session cookies to go with it. This states the rule
    /// that closes it.
    ///
    /// The question is NOT "does this account have a password". Google and Apple accounts are
    /// created with no password at all (<c>AuthProvider</c> "Google"/"Apple"), so a bare
    /// <c>PasswordHash == null</c> test would leave every social account claimable. What makes an
    /// account safe to hand over is that it is passwordless AND has no other way in that somebody
    /// is relying on: no password, no external identity, and no privilege worth stealing.
    ///
    /// Two families qualify, and both are accounts whose owner has never been given any
    /// credential — there is nothing for this to bypass:
    ///   * a guest account this same path created earlier (<c>AuthProvider</c> "Local", no
    ///     password), so a repeat guest booking from the same address keeps landing on the same
    ///     account and the same order history; and
    ///   * an admin-created customer (<c>AuthProvider</c> "Admin", see AdminUsersController's
    ///     register endpoint) — the no-email / phone-booking customers, who must be able to book
    ///     online later and find the cleanings the office entered for them.
    ///
    /// <b><c>Role</c> is checked rather than assumed.</b> The admin endpoint hardcodes
    /// <c>Customer</c>, but the column is mutable afterwards, and a promoted account must not
    /// become claimable as a side effect of a role change nobody connected to this.
    ///
    /// Everyone else — anyone with a password, any Google/Apple account, anything with an
    /// <c>ExternalAuthId</c>, any staff or cleaner account, any blocked or deleted account — is
    /// refused and told to sign in the way they normally do. <b>Never add a bypass parameter
    /// here</b>: the caller is anonymous by definition, so there is no identity that could earn
    /// one.
    /// </summary>
    public static class GuestAccountClaimPolicy
    {
        /// <summary>
        /// What the customer is told when the address they typed belongs to a real account.
        /// Deliberately actionable — they are standing on the payment step and need to know what
        /// to do next, not merely that something was refused.
        /// </summary>
        public const string LoginRequiredMessage =
            "An account already exists for this email address. Please sign in to your account " +
            "and book from there, so this cleaning is saved to your order history.";

        /// <summary>
        /// Auth providers whose accounts carry no credential of their own. Matched
        /// case-insensitively; a null provider reads as "Local", which is what the oldest rows
        /// and the guest path itself leave behind.
        /// </summary>
        private static bool IsCredentiallessProvider(string? authProvider)
        {
            if (string.IsNullOrWhiteSpace(authProvider))
                return true; // legacy rows predate the column being populated — treat as Local

            return authProvider.Equals("Local", StringComparison.OrdinalIgnoreCase)
                || authProvider.Equals("Admin", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the guest checkout may sign this EXISTING account in without a credential.
        /// False means the booking must be refused with <see cref="LoginRequiredMessage"/> — and
        /// the account must not be written to either, since a refused match is a stranger's row.
        /// </summary>
        public static bool MayClaimWithoutCredentials(User? user)
        {
            if (user == null)
                return false;

            // Deleted or blocked: a session for either is one nobody is entitled to. Blocking an
            // account has to mean the account is unreachable, including by this door.
            if (user.IsDeleted || !user.IsActive)
                return false;

            // Has a password — a real registered account, whoever set it and however long ago.
            if (!string.IsNullOrEmpty(user.PasswordHash))
                return false;

            // Signs in through Google or Apple. Passwordless, but emphatically not credentialless:
            // the external identity IS the credential, and this path cannot check it.
            if (!IsCredentiallessProvider(user.AuthProvider))
                return false;

            // Belt and braces for a Local account that has had a social identity linked to it
            // (AuthService's Google branch does exactly that on an existing row). The provider
            // string may still read "Local" while Google is a live way in.
            if (!string.IsNullOrWhiteSpace(user.ExternalAuthId))
                return false;

            // Never mint a staff, admin or cleaner token on an anonymous endpoint.
            if (user.Role != UserRole.Customer)
                return false;

            return true;
        }
    }
}
