using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Helpers
{
    /// <summary>
    /// The rules for <c>ExtraService.ExtraServiceKey</c>, stated once for every writer (the
    /// Booking Services create/update/copy endpoints).
    ///
    /// <para>
    /// The SHAPE is exactly ServiceType.ServiceKey's - lowercase words joined by single hyphens,
    /// uppercase rejected rather than lowered, blank stored as NULL - so the format checks are
    /// delegated to <see cref="ServiceTypeKeyPolicy"/> rather than copied.
    /// </para>
    ///
    /// <para>
    /// UNIQUENESS is where the two differ. The catalogue keeps per-service-type copies of the same
    /// extra (the admin "copy to service type" action), and those copies must share one key, so a
    /// table-wide unique index is impossible. What has to hold is that no service type can SEE two
    /// rows with one key: a type sees its own rows plus every universal row (see
    /// CatalogDtoMapper.ResolveConfiguredExtraServices). So a universal row's key must be unique
    /// across the whole table, and a type-specific row's key must not repeat within its own type
    /// or on any universal row. <see cref="Conflicts"/> is that rule; the database only carries a
    /// plain (ServiceTypeId, ExtraServiceKey) index, because NULL ServiceTypeId (the universal
    /// rows) would slip past a unique index anyway.
    /// </para>
    /// </summary>
    public static class ExtraServiceKeyPolicy
    {
        public const int MaxLength = ServiceTypeKeyPolicy.MaxLength;

        /// <summary>Trims the input; blank (or missing) becomes null, meaning "no key".</summary>
        public static string? Normalize(string? raw) => ServiceTypeKeyPolicy.Normalize(raw);

        /// <summary>Null when the (normalized, non-null) key is well formed; otherwise what is wrong.</summary>
        public static string? DescribeProblem(string key) =>
            ServiceTypeKeyPolicy.DescribeProblem(key)?.Replace("Service key", "Extra service key");

        /// <summary>True for a row every service type sees (no owning type).</summary>
        public static bool IsUniversal(bool isAvailableForAll, int? serviceTypeId) =>
            isAvailableForAll && serviceTypeId == null;

        /// <summary>
        /// True when a row placed at (<paramref name="isAvailableForAll"/>, <paramref name="serviceTypeId"/>)
        /// cannot hold the same key as <paramref name="other"/>: some service type would see both.
        /// </summary>
        public static bool Conflicts(bool isAvailableForAll, int? serviceTypeId, ExtraService other) =>
            IsUniversal(isAvailableForAll, serviceTypeId)
            || IsUniversal(other.IsAvailableForAll, other.ServiceTypeId)
            || (serviceTypeId != null && other.ServiceTypeId == serviceTypeId);

        /// <summary>Message for a key another extra the same service type can see already holds.</summary>
        public static string DescribeDuplicate(string key, string ownerName) =>
            $"Extra service key \"{key}\" is already used by \"{ownerName}\", which the same service type " +
            "can see. Copies of one extra in DIFFERENT service types may share a key; a universal extra's key must be unique.";
    }
}
