using DreamCleaningBackend.Models;
using System.Security.Cryptography;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Placeholder-email machinery for "no-email" customers (User.IsNoEmailUser).
    /// The User.Email column is required and unique across the app, so accounts without a real
    /// email get a generated address under a reserved non-routable domain. Anything that sends
    /// mail or displays an email must treat these as "no email on file".
    /// </summary>
    public static class NoEmailHelper
    {
        /// <summary>Reserved non-routable domain — .invalid is guaranteed by RFC 2606 to never resolve.</summary>
        public const string PlaceholderDomain = "no-email.invalid";

        /// <summary>Generates a unique placeholder like "no-email-3f9a2c1d0b7e@no-email.invalid".
        /// Neutral naming on purpose — these customers pay by cash, Zelle, check, etc.</summary>
        public static string GeneratePlaceholder()
        {
            var bytes = RandomNumberGenerator.GetBytes(6);
            return $"no-email-{Convert.ToHexString(bytes).ToLowerInvariant()}@{PlaceholderDomain}";
        }

        /// <summary>True when the address is a generated no-email placeholder (never send/display it).</summary>
        public static bool IsPlaceholder(string? email)
        {
            return !string.IsNullOrWhiteSpace(email)
                && email.TrimEnd().EndsWith("@" + PlaceholderDomain, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The account's real, sendable email address, or null when it has none (a
        /// no-email customer, or a User that simply wasn't loaded). The ONE rule for "can this
        /// account receive mail at all" - every send path and every admin display resolves it
        /// here, so the panel can never show an address the sender would refuse.</summary>
        public static string? ResolveRealEmail(User? user)
        {
            if (user == null || user.IsNoEmailUser) return null;
            return string.IsNullOrWhiteSpace(user.Email) || IsPlaceholder(user.Email) ? null : user.Email;
        }

        /// <summary>True when the account definitely has NO email on file. False when the User
        /// wasn't loaded - "unknown" must not render as a warning that the customer has no email.</summary>
        public static bool HasNoRealEmail(User? user)
        {
            return user != null && ResolveRealEmail(user) == null;
        }

        /// <summary>The address an order's payment mails actually reach: the order's FROZEN contact
        /// email when it is real, otherwise the owner's account email. Null when neither exists -
        /// those notifications can then only go by text. Shared by the admin send endpoints and by
        /// OrderDtoMapper so the panel promises exactly what the sender will do. A placeholder
        /// contact address (an order transferred from a no-email account keeps one) falls through
        /// to the account, which is the only routable address left.</summary>
        public static string? ResolveOrderNotificationEmail(string? contactEmail, User? user)
        {
            if (!string.IsNullOrWhiteSpace(contactEmail) && !IsPlaceholder(contactEmail))
                return contactEmail;
            return ResolveRealEmail(user);
        }
    }
}
