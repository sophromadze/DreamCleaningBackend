namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// SINGLE SOURCE OF TRUTH for "which registered customer is this lead about?".
    ///
    /// A lead is a PRE-account record: <see cref="Models.Lead.ClientId"/> is only filled in when an
    /// admin creates the lead from an existing order or picks a client by hand. Everything captured
    /// automatically (contact form, quote request, live chat — see <c>LeadCaptureService</c>) carries
    /// nothing but a name, an email and a phone number. So identity has to be resolved on those, and
    /// the rule has to be identical everywhere or the same lead belongs to different people on
    /// different screens.
    ///
    /// The rule, in priority order:
    ///   1. <c>ClientId</c> — an explicit link, always wins.
    ///   2. Email, case-insensitive. Generated no-email placeholders (see <see cref="NoEmailHelper"/>)
    ///      are excluded on BOTH sides: they are non-routable strings on a shared fake domain, so
    ///      matching on them would join unrelated cash customers together.
    ///   3. Phone, digits-normalized. <c>Lead.Phone</c> and <c>User.Phone</c> both normalize on set,
    ///      but user rows predating that still need normalizing on read.
    ///
    /// Phone deliberately comes last: a household shares one number far more often than an email.
    ///
    /// Note this is the IN-MEMORY index, for when a bounded set of users is already loaded.
    /// <c>CrmLeadsController.ExcludeBlockedUserLeadsAsync</c> runs the same three-way rule as a SQL
    /// filter instead, because it narrows a query rather than decorating loaded rows — keep the two
    /// in step if the rule ever changes.
    /// </summary>
    public static class LeadCustomerMatcher
    {
        /// <summary>A user's identity as a lead would carry it.</summary>
        public readonly record struct CustomerIdentity(int Id, string? Email, string? Phone);

        /// <summary>Lower-cased email, or null when absent or a no-email placeholder.</summary>
        public static string? NormalizeEmail(string? email) =>
            string.IsNullOrWhiteSpace(email) || NoEmailHelper.IsPlaceholder(email)
                ? null
                : email.Trim().ToLowerInvariant();

        /// <summary>Bare digits, or null when absent.</summary>
        public static string? NormalizePhone(string? phone) => PhoneHelper.NormalizeToDigits(phone);

        public static LeadCustomerIndex BuildIndex(IEnumerable<CustomerIdentity> customers) =>
            new(customers);
    }

    /// <summary>
    /// Lookup built by <see cref="LeadCustomerMatcher.BuildIndex"/>. Build it once per request over
    /// the customers you actually care about, then match every lead against it.
    /// </summary>
    public sealed class LeadCustomerIndex
    {
        private readonly HashSet<int> _ids = new();
        private readonly Dictionary<string, int> _byEmail = new();
        private readonly Dictionary<string, int> _byPhone = new();

        internal LeadCustomerIndex(IEnumerable<LeadCustomerMatcher.CustomerIdentity> customers)
        {
            // Lowest id wins a contested email/phone (two family members on one number). An
            // arbitrary winner would make the same report return different numbers run to run,
            // so the tie-break is fixed rather than left to enumeration order.
            foreach (var c in customers.OrderBy(c => c.Id))
            {
                _ids.Add(c.Id);

                var email = LeadCustomerMatcher.NormalizeEmail(c.Email);
                if (email != null) _byEmail.TryAdd(email, c.Id);

                var phone = LeadCustomerMatcher.NormalizePhone(c.Phone);
                if (phone != null) _byPhone.TryAdd(phone, c.Id);
            }
        }

        public int Count => _ids.Count;

        /// <summary>
        /// Resolves a lead's identity to one of the indexed customers. False when the lead belongs
        /// to nobody in the index — a genuine prospect, or simply a customer outside the set that
        /// was indexed.
        /// </summary>
        public bool TryMatch(int? clientId, string? leadEmail, string? leadPhone, out int customerId)
        {
            if (clientId.HasValue && _ids.Contains(clientId.Value))
            {
                customerId = clientId.Value;
                return true;
            }

            var email = LeadCustomerMatcher.NormalizeEmail(leadEmail);
            if (email != null && _byEmail.TryGetValue(email, out customerId)) return true;

            var phone = LeadCustomerMatcher.NormalizePhone(leadPhone);
            if (phone != null && _byPhone.TryGetValue(phone, out customerId)) return true;

            customerId = 0;
            return false;
        }
    }
}
