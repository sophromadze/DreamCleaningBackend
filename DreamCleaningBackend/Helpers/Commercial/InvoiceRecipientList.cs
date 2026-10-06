using System.Text.Json;

namespace DreamCleaningBackend.Helpers.Commercial
{
    /// <summary>
    /// Who a commercial invoice is emailed to (2026-10).
    ///
    /// Each invoice carries its OWN list (<c>CommercialInvoice.RecipientEmailsJson</c>): sending,
    /// resending, reminders and receipts all go to exactly that list. It is pre-filled from the
    /// client — their saved default list when an admin has saved one, otherwise the billing
    /// contact's email (or the notice email) that a single-recipient invoice always went to.
    ///
    /// Stored as a JSON array, the same shape as the invoice's service dates: the list is only
    /// ever read and written whole, with the invoice.
    ///
    /// Normalising is deliberately conservative: trimmed, duplicates removed case-insensitively,
    /// first spelling and order kept. Addresses are never lower-cased — the local part of an
    /// address is technically case-sensitive, and rewriting what the admin typed is not ours to do.
    /// </summary>
    public static class InvoiceRecipientList
    {
        /// <summary>Enough for any accounts-payable team; a list longer than this is a paste error.</summary>
        public const int MaxRecipients = 20;

        /// <summary>Matches the email-log and reminder columns the addresses are written into.</summary>
        public const int MaxAddressLength = 255;

        /// <summary>
        /// Trims, drops blanks and case-insensitive duplicates, keeps the order. Does not validate —
        /// see <see cref="DescribeProblem"/>.
        /// </summary>
        public static List<string> Normalize(IEnumerable<string?>? addresses)
        {
            var result = new List<string>();
            if (addresses == null) return result;

            foreach (var raw in addresses)
            {
                var email = raw?.Trim();
                if (string.IsNullOrEmpty(email)) continue;
                if (result.Any(e => string.Equals(e, email, StringComparison.OrdinalIgnoreCase))) continue;
                result.Add(email);
            }

            return result;
        }

        /// <summary>
        /// Why the (already normalised) list cannot be used, or null when it can. Names the
        /// offending address, because "one of your addresses is invalid" on a list of six is a
        /// puzzle, not an error message.
        /// </summary>
        public static string? DescribeProblem(IReadOnlyList<string> recipients)
        {
            if (recipients.Count == 0)
                return "Add at least one email address to send this invoice to.";

            if (recipients.Count > MaxRecipients)
                return $"An invoice can be sent to at most {MaxRecipients} addresses.";

            foreach (var email in recipients)
            {
                if (email.Length > MaxAddressLength)
                    return $"\"{Shorten(email)}\" is too long to be an email address.";

                var problem = EmailAddressValidator.DescribeProblem(email);
                if (problem != null)
                    return $"\"{email}\": {problem}";
            }

            return null;
        }

        public static string? Serialize(IReadOnlyList<string> recipients) =>
            recipients.Count == 0 ? null : JsonSerializer.Serialize(recipients);

        /// <summary>The stored list. A malformed value reads as empty rather than taking the page down.</summary>
        public static List<string> Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try
            {
                return Normalize(JsonSerializer.Deserialize<List<string>>(json));
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// What a NEW invoice for a client is pre-addressed to: the client's saved default list,
        /// otherwise the one address a single-recipient invoice used to go to (the billing
        /// contact's email, then the client's notice email). Empty when the client has none.
        /// </summary>
        public static List<string> DefaultsFor(string? savedDefaultsJson, string? billingContactEmail, string? noticeEmail)
        {
            var saved = Parse(savedDefaultsJson);
            if (saved.Count > 0) return saved;

            var fallback = !string.IsNullOrWhiteSpace(billingContactEmail) ? billingContactEmail : noticeEmail;
            return Normalize(new[] { fallback });
        }

        /// <summary>"a@x.com, b@y.com" — for the activity trail and the send history.</summary>
        public static string Describe(IEnumerable<string> recipients) => string.Join(", ", recipients);

        private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "…";
    }
}
