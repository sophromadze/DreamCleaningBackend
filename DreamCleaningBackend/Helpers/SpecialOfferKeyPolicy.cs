namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The rules for <c>SpecialOffer.OfferKey</c>, stated once for the special-offer create/update
    /// endpoints.
    ///
    /// <para>
    /// The SHAPE is exactly ServiceType.ServiceKey's - lowercase words joined by single hyphens,
    /// uppercase rejected rather than lowered, blank stored as NULL - so the format checks are
    /// delegated to <see cref="ServiceTypeKeyPolicy"/> rather than copied. A key is unique across all
    /// offers when set (a unique index backs it; MariaDB allows any number of NULLs).
    /// </para>
    ///
    /// <para>
    /// Only a SuperAdmin may set, change or clear a key - the special-offers tab is open to Admins,
    /// but which row the site treats as the first-time offer is not an everyday edit.
    /// </para>
    /// </summary>
    public static class SpecialOfferKeyPolicy
    {
        public const int MaxLength = ServiceTypeKeyPolicy.MaxLength;

        /// <summary>Trims the input; blank (or missing) becomes null, meaning "no key".</summary>
        public static string? Normalize(string? raw) => ServiceTypeKeyPolicy.Normalize(raw);

        /// <summary>Null when the (normalized, non-null) key is well formed; otherwise what is wrong.</summary>
        public static string? DescribeProblem(string key) =>
            ServiceTypeKeyPolicy.DescribeProblem(key)?.Replace("Service key", "Offer key");

        /// <summary>Message for a key another offer already holds.</summary>
        public static string DescribeDuplicate(string key, string ownerName) =>
            $"Offer key \"{key}\" is already used by \"{ownerName}\". Each special offer needs its own key.";

        public const string SuperAdminOnlyMessage = "Only a SuperAdmin can change a special offer's key.";
    }
}
