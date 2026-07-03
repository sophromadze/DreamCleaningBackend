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
    }
}
