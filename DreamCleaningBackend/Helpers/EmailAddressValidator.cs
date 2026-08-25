using System.Text.RegularExpressions;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// Email-format checking that explains ITSELF, for endpoints an ADMIN types into.
    ///
    /// <para>
    /// Mirrored, message for message, by <c>DreamCleaningNG/src/app/utils/email.utils.ts</c>
    /// (<c>describeEmailProblem</c>). Change the wording in both files together, or the same typo
    /// is described differently depending on whether it was caught in the browser or on the wire.
    /// </para>
    ///
    /// <para>
    /// Why this exists (2026-08): an admin registered a customer and typed an address with no
    /// '@'. The DTO carried <c>[EmailAddress]</c>, so the rejection came from <c>[ApiController]</c>
    /// automatic model validation — a <c>ValidationProblemDetails</c> body, which has an
    /// <c>errors</c> dictionary and NO <c>message</c> property. The admin panel read only
    /// <c>err.error.message</c>, so what reached the admin was the bare transport text
    /// "Http failure response for .../users/register: 400". Endpoints admins use interactively
    /// should validate the address themselves and answer with the same
    /// <c>BadRequest(new { message = ... })</c> shape as every other failure branch.
    /// </para>
    ///
    /// <para>
    /// Deliberately NOT an RFC 5322 implementation. Its only job is to produce a sentence a
    /// non-technical admin can act on; delivery remains the real test of an address.
    /// </para>
    /// </summary>
    public static class EmailAddressValidator
    {
        /// <summary>The shape shown to the user in every message here. One spelling, one place.</summary>
        public const string Example = "name@example.com";

        /// <summary>
        /// Final shape check, applied only after the specific checks below have all passed — so it
        /// can only fire for something exotic (stray ',', '&lt;', quotes) with no dedicated message.
        /// </summary>
        private static readonly Regex Shape =
            new(@"^[^\s@,<>()\[\];:""]+@[^\s@,<>()\[\];:""]+\.[A-Za-z]{2,}$", RegexOptions.Compiled);

        /// <summary>
        /// Returns a sentence describing what is wrong with <paramref name="rawEmail"/>, or null
        /// when it looks usable. The ORDER of the checks is the point: the first thing wrong with
        /// the address is the thing the person needs to be told about. A generic
        /// "invalid email address" is exactly the message that failed the admin in the incident above.
        /// </summary>
        public static string? DescribeProblem(string? rawEmail)
        {
            var email = (rawEmail ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(email))
                return "Email address is required.";

            if (email.Any(char.IsWhiteSpace))
                return $"Email address cannot contain spaces. It should look like {Example}.";

            var atCount = email.Count(c => c == '@');
            if (atCount == 0)
                return $"Email address is missing the \"@\" symbol. It should look like {Example}.";
            if (atCount > 1)
                return $"Email address has {atCount} \"@\" symbols — it should have exactly one, like {Example}.";

            var parts = email.Split('@');
            var localPart = parts[0];
            var domain = parts[1];

            if (string.IsNullOrEmpty(localPart))
                return $"Email address is missing the part before the \"@\". It should look like {Example}.";
            if (string.IsNullOrEmpty(domain))
                return $"Email address is missing the part after the \"@\" (the domain). It should look like {Example}.";
            if (!domain.Contains('.'))
                return $"Email domain \"{domain}\" is missing its ending, such as \".com\". It should look like {Example}.";
            if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains(".."))
                return $"Email domain \"{domain}\" does not look right — check the dots. It should look like {Example}.";

            var tld = domain[(domain.LastIndexOf('.') + 1)..];
            if (tld.Length < 2 || !tld.All(char.IsAsciiLetter))
                return $"Email domain \"{domain}\" does not end in a valid domain ending such as \".com\". It should look like {Example}.";

            if (!Shape.IsMatch(email))
                return $"Email address contains characters that are not allowed. It should look like {Example}.";

            return null;
        }

        /// <summary>Convenience predicate over <see cref="DescribeProblem"/>.</summary>
        public static bool IsValid(string? rawEmail) => DescribeProblem(rawEmail) is null;
    }
}
